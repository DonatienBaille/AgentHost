using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Le journal d'audit doit être append-only (spécification §13, WORM). Sa valeur tient entièrement
/// à ce qu'on ne puisse pas le réécrire : un journal effaçable ne prouve rien.
///
/// Rien ne l'empêchait jusqu'à la migration 0008 — un UPDATE ou un DELETE ordinaire passait. Ces
/// tests exercent les trois ordres depuis la connexion applicative elle-même, c'est-à-dire avec
/// exactement les droits dont dispose le code de production, et non depuis une session privilégiée
/// qui prouverait moins.
///
/// TRUNCATE a son propre test parce qu'il a son propre trigger : un trigger BEFORE DELETE
/// FOR EACH ROW ne voit jamais un TRUNCATE, qui est justement le raccourci qu'emprunterait
/// quelqu'un voulant vider le journal d'un coup.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuditLogWormTests
{
    private readonly AgentHostApiFactory _factory;

    public AuditLogWormTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_audit_entry_can_still_be_written()
    {
        // Le garde-fou ne doit pas casser ce que le journal existe pour faire.
        var (orgId, entryId) = await RecordAsync("worm.write");

        Assert.Equal("worm.write", await ScalarAsync(
            "SELECT action FROM audit_log WHERE id = @Id", new { Id = entryId }));
        Assert.NotNull(orgId);
    }

    [Fact]
    public async Task Updating_an_audit_entry_is_refused_and_leaves_it_intact()
    {
        var (_, entryId) = await RecordAsync("worm.update");

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "UPDATE audit_log SET action = 'tampered' WHERE id = @Id", new { Id = entryId }));

        Assert.Contains("append-only", error.MessageText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("worm.update", await ScalarAsync(
            "SELECT action FROM audit_log WHERE id = @Id", new { Id = entryId }));
    }

    [Fact]
    public async Task Deleting_an_audit_entry_is_refused_and_leaves_it_intact()
    {
        var (_, entryId) = await RecordAsync("worm.delete");

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM audit_log WHERE id = @Id", new { Id = entryId }));

        Assert.Contains("append-only", error.MessageText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("worm.delete", await ScalarAsync(
            "SELECT action FROM audit_log WHERE id = @Id", new { Id = entryId }));
    }

    [Fact]
    public async Task Truncating_the_audit_log_is_refused()
    {
        var (_, entryId) = await RecordAsync("worm.truncate");

        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            ExecuteAsync("TRUNCATE audit_log", new { }));

        Assert.Contains("append-only", error.MessageText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("worm.truncate", await ScalarAsync(
            "SELECT action FROM audit_log WHERE id = @Id", new { Id = entryId }));
    }

    [Fact]
    public async Task A_refused_deletion_does_not_take_the_rest_of_the_transaction_with_it()
    {
        // Le trigger lève, donc la transaction qui portait le DELETE est perdue — c'est attendu.
        // Ce qui ne doit PAS arriver, c'est que le journal devienne inutilisable ensuite : une
        // écriture ultérieure sur une nouvelle connexion doit continuer de passer.
        var (_, entryId) = await RecordAsync("worm.recovery");

        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            "DELETE FROM audit_log WHERE id = @Id", new { Id = entryId }));

        var (_, afterId) = await RecordAsync("worm.recovery.after");
        Assert.Equal("worm.recovery.after", await ScalarAsync(
            "SELECT action FROM audit_log WHERE id = @Id", new { Id = afterId }));
    }

    // ---- helpers ----

    /// <summary>
    /// Écrit une entrée par le vrai chemin applicatif (<see cref="IAuditService"/>), pas par un
    /// INSERT direct : si un jour l'écriture passait par une vue ou une procédure, ce test
    /// continuerait de tester ce que fait réellement le produit.
    /// </summary>
    private async Task<(string OrgId, string EntryId)> RecordAsync(string action)
    {
        var auth = await TestData.RegisterAsync(_factory.CreateClient());
        var orgId = auth.User.OrgId;

        using var scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        await audit.RecordAsync(orgId, action, auth.User.Id, "test", UlidGenerator.NewUlid());

        var entryId = await ScalarAsync(
            "SELECT id FROM audit_log WHERE org_id = @OrgId AND action = @Action ORDER BY created_at DESC LIMIT 1",
            new { OrgId = orgId, Action = action });

        Assert.NotNull(entryId);
        return (orgId, entryId!);
    }

    private async Task ExecuteAsync(string sql, object parameters)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var db = factory.CreateConnection();
        await db.ExecuteAsync(sql, parameters);
    }

    private async Task<string?> ScalarAsync(string sql, object parameters)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var db = factory.CreateConnection();
        var value = await db.ExecuteScalarAsync<object?>(sql, parameters);
        return value?.ToString();
    }
}
