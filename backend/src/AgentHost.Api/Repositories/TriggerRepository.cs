using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Dapper;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>
/// Les déclencheurs entrants (feuille de route, lot 4).
///
/// <b>Deux familles de lecture, et elles n'ont pas le même périmètre.</b> Les méthodes de gestion
/// sont scopées à l'organisation de l'appelant, comme tout le reste du dépôt. L'ingestion d'un
/// webhook et le réveil d'une échéance cron, eux, n'ont pas d'appelant authentifié : ils lisent
/// sans périmètre, et c'est <b>la signature</b> (webhook) ou <b>l'échéance</b> (cron) qui autorise.
/// Ces méthodes-là portent le mot <c>Unscoped</c> dans leur nom pour qu'aucune ne soit appelée par
/// distraction depuis un chemin qui devrait, lui, être scopé.
/// </summary>
public interface ITriggerRepository
{
    Task<Trigger?> GetAsync(string id, string orgId, CancellationToken ct = default);
    Task<List<Trigger>> ListByProjectAsync(string projectId, string orgId, CancellationToken ct = default);
    Task InsertAsync(Trigger trigger, CancellationToken ct = default);
    Task UpdateAsync(Trigger trigger, CancellationToken ct = default);
    Task<bool> SoftDeleteAsync(string id, string orgId, CancellationToken ct = default);

    /// <summary>
    /// Le déclencheur visé par une livraison entrante. Sans périmètre : la requête est anonyme, et
    /// c'est la vérification de signature qui décide ensuite.
    /// </summary>
    Task<Trigger?> GetForIngestUnscopedAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Réclame une échéance cron échue, en une seule instruction atomique.
    ///
    /// <b>C'est la pièce qui rend le planificateur sûr à plusieurs répliques.</b> Lire puis écrire
    /// laisserait deux instances lire la même échéance avant que l'une n'écrive, et l'agent
    /// partirait deux fois. Ici, l'<c>UPDATE … WHERE next_run_at = @Expected RETURNING</c> ne rend
    /// une ligne qu'à celle qui a gagné : la seconde ne voit rien et n'a rien à faire.
    /// </summary>
    Task<Trigger?> ClaimDueAsync(string id, DateTime expectedNextRunAt, DateTime? newNextRunAt, CancellationToken ct = default);

    /// <summary>Les échéances cron échues, sans périmètre : le planificateur sert toute l'installation.</summary>
    Task<List<Trigger>> ListDueUnscopedAsync(DateTime nowUtc, int limit, CancellationToken ct = default);

    /// <summary>Enregistre le run produit par un déclenchement.</summary>
    Task RecordFiringAsync(string triggerId, string runId, DateTime firedAt, CancellationToken ct = default);

    /// <summary>
    /// Mémorise une livraison, et dit si elle est nouvelle.
    ///
    /// <c>false</c> = déjà vue : c'est une réémission, et relancer l'agent la facturerait deux
    /// fois. L'unicité est portée par la clé primaire, pas par une lecture préalable, parce que
    /// deux réémissions simultanées passeraient toutes les deux une vérification applicative.
    /// </summary>
    Task<bool> TryRecordDeliveryAsync(string triggerId, string deliveryId, CancellationToken ct = default);

    /// <summary>Attache le run à une livraison déjà enregistrée, pour que la trace soit complète.</summary>
    Task AttachDeliveryRunAsync(string triggerId, string deliveryId, string runId, CancellationToken ct = default);
}

public class TriggerRepository : ITriggerRepository
{
    private const string Columns = """
        id, org_id, project_id, agent_id, type, name, is_active, inputs,
        secret_encrypted, provider, event_filter,
        cron_expression, timezone, next_run_at, last_run_at, last_run_id,
        created_at, updated_at, deleted_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public TriggerRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Trigger?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {Columns} FROM triggers
            WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<TriggerRow>(
            new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<List<Trigger>> ListByProjectAsync(string projectId, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {Columns} FROM triggers
            WHERE project_id = @ProjectId AND org_id = @OrgId AND deleted_at IS NULL
            ORDER BY created_at DESC
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<TriggerRow>(
            new CommandDefinition(sql, new { ProjectId = projectId, OrgId = orgId }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<Trigger?> GetForIngestUnscopedAsync(string id, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {Columns} FROM triggers
            WHERE id = @Id AND type = 'webhook' AND is_active AND deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<TriggerRow>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task InsertAsync(Trigger trigger, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO triggers (
                id, org_id, project_id, agent_id, type, name, is_active, inputs,
                secret_encrypted, provider, event_filter,
                cron_expression, timezone, next_run_at, last_run_at, last_run_id,
                created_at, updated_at)
            VALUES (
                @Id, @OrgId, @ProjectId, @AgentId, @Type, @Name, @IsActive, @Inputs::jsonb,
                @SecretEncrypted, @Provider, @EventFilter::jsonb,
                @CronExpression, @Timezone, @NextRunAt, @LastRunAt, @LastRunId,
                @CreatedAt, @UpdatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, TriggerRow.FromDomain(trigger), cancellationToken: ct));
        _logger.Information("Trigger {TriggerId} created ({Type}) for agent {AgentId}",
            trigger.Id, trigger.Type.ToDbString(), trigger.AgentId);
    }

    public async Task UpdateAsync(Trigger trigger, CancellationToken ct = default)
    {
        // Le secret n'est jamais réécrit ici : le remplacer suppose de le régénérer, ce qui est un
        // geste explicite (et qui doit rendre la nouvelle valeur une fois, comme à la création).
        const string sql = """
            UPDATE triggers SET
                name = @Name,
                is_active = @IsActive,
                inputs = @Inputs::jsonb,
                event_filter = @EventFilter::jsonb,
                cron_expression = @CronExpression,
                timezone = @Timezone,
                next_run_at = @NextRunAt,
                updated_at = @UpdatedAt
            WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, TriggerRow.FromDomain(trigger), cancellationToken: ct));
    }

    public async Task<bool> SoftDeleteAsync(string id, string orgId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE triggers SET deleted_at = NOW(), is_active = FALSE, next_run_at = NULL
            WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task<List<Trigger>> ListDueUnscopedAsync(DateTime nowUtc, int limit, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {Columns} FROM triggers
            WHERE type = 'cron' AND is_active AND deleted_at IS NULL
              AND next_run_at IS NOT NULL AND next_run_at <= @Now
            ORDER BY next_run_at
            LIMIT @Limit
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<TriggerRow>(
            new CommandDefinition(sql, new { Now = nowUtc, Limit = limit }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<Trigger?> ClaimDueAsync(
        string id, DateTime expectedNextRunAt, DateTime? newNextRunAt, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE triggers SET next_run_at = @NewNextRunAt, updated_at = NOW()
            WHERE id = @Id AND next_run_at = @ExpectedNextRunAt
              AND type = 'cron' AND is_active AND deleted_at IS NULL
            RETURNING {Columns}
            """;
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<TriggerRow>(new CommandDefinition(
            sql,
            new { Id = id, ExpectedNextRunAt = expectedNextRunAt, NewNextRunAt = newNextRunAt },
            cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task RecordFiringAsync(string triggerId, string runId, DateTime firedAt, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE triggers SET last_run_at = @FiredAt, last_run_id = @RunId, updated_at = NOW()
            WHERE id = @Id
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(
            sql, new { Id = triggerId, RunId = runId, FiredAt = firedAt }, cancellationToken: ct));
    }

    public async Task<bool> TryRecordDeliveryAsync(string triggerId, string deliveryId, CancellationToken ct = default)
    {
        // ON CONFLICT DO NOTHING : la clé primaire tranche, pas une lecture préalable. Deux
        // réémissions concurrentes passeraient toutes deux un `SELECT … IF NOT EXISTS`.
        const string sql = """
            INSERT INTO trigger_deliveries (trigger_id, delivery_id, received_at)
            VALUES (@TriggerId, @DeliveryId, NOW())
            ON CONFLICT (trigger_id, delivery_id) DO NOTHING
            """;
        using var db = _connectionFactory.CreateConnection();
        var inserted = await db.ExecuteAsync(new CommandDefinition(
            sql, new { TriggerId = triggerId, DeliveryId = deliveryId }, cancellationToken: ct));
        return inserted > 0;
    }

    public async Task AttachDeliveryRunAsync(string triggerId, string deliveryId, string runId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE trigger_deliveries SET run_id = @RunId
            WHERE trigger_id = @TriggerId AND delivery_id = @DeliveryId
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(
            sql, new { TriggerId = triggerId, DeliveryId = deliveryId, RunId = runId }, cancellationToken: ct));
    }
}

/// <summary>
/// Forme SQL d'un déclencheur : les colonnes d'énumération sont des chaînes, jamais des enums.
/// Voir l'en-tête de <c>DbRows.cs</c> — Dapper court-circuite les enums dans les deux sens.
/// </summary>
internal sealed class TriggerRow
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public JsonNode? Inputs { get; set; }

    public byte[]? SecretEncrypted { get; set; }
    public string? Provider { get; set; }
    public JsonNode? EventFilter { get; set; }

    public string? CronExpression { get; set; }
    public string Timezone { get; set; } = "UTC";
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Trigger ToDomain() => new()
    {
        Id = Id,
        OrgId = OrgId,
        ProjectId = ProjectId,
        AgentId = AgentId,
        Type = TriggerTypeExtensions.FromDbString(Type),
        Name = Name,
        IsActive = IsActive,
        Inputs = Inputs,
        SecretEncrypted = SecretEncrypted,
        Provider = Provider is null ? null : TriggerProviderExtensions.FromDbString(Provider),
        EventFilter = DeserializeFilter(EventFilter),
        CronExpression = CronExpression,
        TimeZone = Timezone,
        NextRunAt = NextRunAt,
        LastRunAt = LastRunAt,
        LastRunId = LastRunId,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        DeletedAt = DeletedAt,
    };

    public static TriggerRow FromDomain(Trigger t) => new()
    {
        Id = t.Id,
        OrgId = t.OrgId,
        ProjectId = t.ProjectId,
        AgentId = t.AgentId,
        Type = t.Type.ToDbString(),
        Name = t.Name,
        IsActive = t.IsActive,
        Inputs = t.Inputs ?? new JsonObject(),
        SecretEncrypted = t.SecretEncrypted,
        Provider = t.Provider?.ToDbString(),
        EventFilter = SerializeFilter(t.EventFilter),
        CronExpression = t.CronExpression,
        Timezone = t.TimeZone,
        NextRunAt = t.NextRunAt,
        LastRunAt = t.LastRunAt,
        LastRunId = t.LastRunId,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
        DeletedAt = t.DeletedAt,
    };

    /// <summary>Camel case, comme partout ailleurs : le JSONB stocké se relit à l'œil nu en SQL.</summary>
    private static readonly JsonSerializerOptions FilterJson = new(JsonSerializerDefaults.Web);

    private static TriggerEventFilter? DeserializeFilter(JsonNode? node) =>
        node is null ? null : node.Deserialize<TriggerEventFilter>(FilterJson);

    private static JsonNode? SerializeFilter(TriggerEventFilter? filter) =>
        filter is null ? null : JsonSerializer.SerializeToNode(filter, FilterJson);
}
