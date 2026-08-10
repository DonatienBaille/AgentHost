using AgentHost.Api.Infrastructure;
using AgentHost.Api.Services.Email;
using Dapper;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>Un message en attente d'acheminement, tel qu'il vit en base.</summary>
public sealed class OutboxEntry
{
    public string Id { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public byte[] BodyEncrypted { get; set; } = [];
    public string Kind { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime? AbandonedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// La file d'envoi durable (dette identifiée, hors lots).
///
/// <b>Toutes les méthodes sont sans périmètre d'organisation, et c'est correct :</b> un courriel
/// transactionnel n'appartient pas à un locataire — une réinitialisation part vers une adresse qui
/// n'a peut-être aucun compte, et une invitation vers quelqu'un qui n'en a pas encore. Il n'y a
/// aucune surface HTTP au-dessus de cette table : seul le répartiteur la lit.
/// </summary>
public interface IEmailOutboxRepository
{
    Task InsertAsync(OutboxEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Réclame un message dû, en une seule instruction atomique.
    ///
    /// Même dispositif que le planificateur de déclencheurs : avec plusieurs répliques, deux
    /// répartiteurs voient le même message et l'enverraient tous les deux. Le <c>UPDATE …
    /// RETURNING</c> n'en sert qu'un — l'autre passe au suivant. Un courriel envoyé deux fois n'est
    /// pas une catastrophe, mais un lien de réinitialisation dupliqué dans deux messages est une
    /// confusion gratuite pour l'utilisateur.
    /// </summary>
    Task<OutboxEntry?> ClaimNextDueAsync(DateTime nowUtc, TimeSpan lease, CancellationToken ct = default);

    /// <summary>Remise réussie : la ligne disparaît. Voir la migration 0013 pour le pourquoi.</summary>
    Task DeleteAsync(string id, CancellationToken ct = default);

    /// <summary>Échec : on note la raison et on repousse la prochaine tentative.</summary>
    Task RescheduleAsync(string id, DateTime nextAttemptAt, string error, CancellationToken ct = default);

    /// <summary>Renoncement définitif : la ligne reste, visible, et n'est plus réessayée.</summary>
    Task AbandonAsync(string id, string error, CancellationToken ct = default);

    /// <summary>Nombre de messages encore en attente. Sert au diagnostic et aux tests.</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);
}

public class EmailOutboxRepository : IEmailOutboxRepository
{
    private const string Columns = """
        id, to_address, subject, body_encrypted, kind,
        attempts, next_attempt_at, last_error, abandoned_at, created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public EmailOutboxRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO email_outbox (id, to_address, subject, body_encrypted, kind, attempts, next_attempt_at, created_at)
            VALUES (@Id, @ToAddress, @Subject, @BodyEncrypted, @Kind, @Attempts, @NextAttemptAt, @CreatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, entry, cancellationToken: ct));
    }

    public async Task<OutboxEntry?> ClaimNextDueAsync(
        DateTime nowUtc, TimeSpan lease, CancellationToken ct = default)
    {
        // Le bail repousse `next_attempt_at` avant même l'envoi : si le processus meurt pendant la
        // remise, la ligne redevient candidate à l'expiration du bail plutôt que de rester réservée
        // pour toujours. Sans lui, un plantage au mauvais moment gèlerait le message.
        var sql = $"""
            UPDATE email_outbox SET
                attempts = attempts + 1,
                next_attempt_at = @LeaseUntil
            WHERE id = (
                SELECT id FROM email_outbox
                WHERE abandoned_at IS NULL AND next_attempt_at <= @Now
                ORDER BY next_attempt_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            RETURNING {Columns}
            """;

        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<OutboxEntry>(new CommandDefinition(
            sql, new { Now = nowUtc, LeaseUntil = nowUtc + lease }, cancellationToken: ct));
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM email_outbox WHERE id = @Id", new { Id = id }, cancellationToken: ct));
    }

    public async Task RescheduleAsync(string id, DateTime nextAttemptAt, string error, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE email_outbox SET next_attempt_at = @NextAttemptAt, last_error = @Error
            WHERE id = @Id
            """, new { Id = id, NextAttemptAt = nextAttemptAt, Error = Truncate(error) }, cancellationToken: ct));
    }

    public async Task AbandonAsync(string id, string error, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition("""
            UPDATE email_outbox SET abandoned_at = NOW(), last_error = @Error WHERE id = @Id
            """, new { Id = id, Error = Truncate(error) }, cancellationToken: ct));

        _logger.Error("Courriel {OutboxId} abandonné après épuisement des tentatives : {Error}", id, Truncate(error));
    }

    public async Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM email_outbox WHERE abandoned_at IS NULL", cancellationToken: ct));
    }

    /// <summary>Un message d'erreur n'a pas à faire enfler une ligne ; les mille premiers caractères disent tout.</summary>
    private static string Truncate(string error) => error.Length <= 1000 ? error : error[..1000];
}
