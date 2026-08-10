using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Services.Email;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHost.Api.Tests;

/// <summary>
/// Une file d'envoi en mémoire, qui respecte le même contrat que la vraie.
///
/// Les tests du répartiteur portent sur <b>son</b> comportement — persistance avant remise,
/// réessais, renoncement — et non sur le SQL, qui a ses propres tests d'intégration. Un double en
/// mémoire les rend rapides et déterministes ; il reproduit soigneusement les deux propriétés dont
/// le répartiteur dépend : la réclamation incrémente le compteur de tentatives, et une remise
/// réussie supprime la ligne.
/// </summary>
public sealed class InMemoryOutbox : IEmailOutboxRepository
{
    private readonly List<OutboxEntry> _entries = [];
    private readonly object _gate = new();

    /// <summary>Tout ce qui a été inséré, y compris ce qui a été supprimé depuis.</summary>
    public List<OutboxEntry> Inserted { get; } = [];

    public List<string> Deleted { get; } = [];
    public List<string> Abandoned { get; } = [];

    public Task InsertAsync(OutboxEntry entry, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Inserted.Add(entry);
        }
        return Task.CompletedTask;
    }

    public Task<OutboxEntry?> ClaimNextDueAsync(DateTime nowUtc, TimeSpan lease, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var due = _entries
                .Where(e => e.AbandonedAt is null && e.NextAttemptAt <= nowUtc)
                .OrderBy(e => e.NextAttemptAt)
                .FirstOrDefault();

            if (due is null) return Task.FromResult<OutboxEntry?>(null);

            // Comme le SQL : le compteur monte à la réclamation, pas à l'échec. Le répartiteur s'en
            // sert pour choisir le recul, et décaler d'un ferait sauter la première attente.
            due.Attempts++;
            due.NextAttemptAt = nowUtc + lease;

            return Task.FromResult<OutboxEntry?>(new OutboxEntry
            {
                Id = due.Id,
                ToAddress = due.ToAddress,
                Subject = due.Subject,
                BodyEncrypted = due.BodyEncrypted,
                Kind = due.Kind,
                Attempts = due.Attempts,
                NextAttemptAt = due.NextAttemptAt,
                CreatedAt = due.CreatedAt,
            });
        }
    }

    public Task DeleteAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == id);
            Deleted.Add(id);
        }
        return Task.CompletedTask;
    }

    public Task RescheduleAsync(string id, DateTime nextAttemptAt, string error, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                entry.NextAttemptAt = nextAttemptAt;
                entry.LastError = error;
            }
        }
        return Task.CompletedTask;
    }

    public Task AbandonAsync(string id, string error, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null)
            {
                entry.AbandonedAt = DateTime.UtcNow;
                entry.LastError = error;
            }
            Abandoned.Add(id);
        }
        return Task.CompletedTask;
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_entries.Count(e => e.AbandonedAt is null));
    }

    /// <summary>Rend une entrée immédiatement due, pour ne pas attendre un recul réel en test.</summary>
    public void MakeDue(string id)
    {
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is not null) entry.NextAttemptAt = DateTime.UtcNow.AddSeconds(-1);
        }
    }

    public IReadOnlyList<OutboxEntry> Snapshot()
    {
        lock (_gate) return _entries.Select(Clone).ToList();
    }

    private static OutboxEntry Clone(OutboxEntry e) => new()
    {
        Id = e.Id,
        ToAddress = e.ToAddress,
        Subject = e.Subject,
        BodyEncrypted = e.BodyEncrypted,
        Kind = e.Kind,
        Attempts = e.Attempts,
        NextAttemptAt = e.NextAttemptAt,
        LastError = e.LastError,
        AbandonedAt = e.AbandonedAt,
        CreatedAt = e.CreatedAt,
    };
}

/// <summary>
/// Un chiffrement réversible et trivial, uniquement pour que le répartiteur ait un
/// <see cref="ISecretsBroker"/> à appeler.
///
/// <b>Ce n'est pas un raccourci sur la sécurité</b> : le vrai chiffrement a ses propres tests
/// (<c>SecretsKeyRotationTests</c>). Ce que les tests du répartiteur doivent voir, c'est que le
/// corps <b>passe</b> par le chiffrement à l'écriture et par le déchiffrement à la lecture — et un
/// double le montre mieux qu'un vrai, dont la sortie serait opaque à l'assertion.
/// </summary>
public sealed class ReversibleSecretsBroker : ISecretsBroker
{
    public byte[] Encrypt(string plaintext) =>
        System.Text.Encoding.UTF8.GetBytes("chiffré:" + plaintext);

    public string Decrypt(byte[] encryptedValue) =>
        System.Text.Encoding.UTF8.GetString(encryptedValue)["chiffré:".Length..];

    public bool IsEncryptedWithCurrentKey(byte[] encryptedValue) => true;

    public int PreviousKeyCount => 0;

    public Task<Dictionary<string, string>> ResolveForRunAsync(
        string orgId, string? projectId, string runId, IEnumerable<string> secretNames,
        CancellationToken ct = default) => Task.FromResult(new Dictionary<string, string>());
}

/// <summary>Fabriques partagées pour les tests du répartiteur de courriels.</summary>
public static class TestDispatcher
{
    public static IServiceScopeFactory ScopeFactory() =>
        new ServiceCollection()
            .AddScoped<ISecretsBroker, ReversibleSecretsBroker>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    public static BackgroundEmailDispatcher Create(
        IEmailSender sender, IEmailOutboxRepository outbox, Serilog.ILogger logger) =>
        new(sender, outbox, ScopeFactory(), logger);
}
