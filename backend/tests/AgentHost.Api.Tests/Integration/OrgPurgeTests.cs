using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// L'effacement définitif d'une organisation (dette identifiée — « purge RGPD réelle »).
///
/// <b>Pourquoi ces tests portent sur la vraie base, et pas sur un double.</b> Ce que la purge doit
/// prouver n'est pas qu'elle appelle les bonnes méthodes : c'est qu'il ne <b>reste rien</b>. Une
/// table oubliée ne produit aucune erreur — elle produit des lignes orphelines que plus aucun
/// chemin applicatif ne sait atteindre, c'est-à-dire des données personnelles devenues invisibles,
/// ce qui est pire que de ne rien avoir effacé.
///
/// Les deux tests qui comptent le plus sont donc : <b>rien ne subsiste</b> de l'organisation
/// effacée, et <b>rien ne bouge</b> chez la voisine. Une purge qui déborde est une perte de
/// données ; une purge qui laisse traîner est une non-conformité.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class OrgPurgeTests
{
    private readonly AgentHostApiFactory _factory;

    public OrgPurgeTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_purged_organization_leaves_nothing_behind_in_any_table()
    {
        var org = await PopulatedOrgAsync();

        var report = await PurgeAsync(org.OrgId);

        Assert.True(report.Found);
        Assert.True(report.TotalRows > 0);

        // Le décompte table par table, plutôt qu'un « ça a marché » : c'est la seule assertion qui
        // attrape une table oubliée, et elle est confrontée au schéma par un test à part.
        foreach (var table in OrgPurgeService.PurgedTables)
            Assert.Equal(0, await CountForOrgAsync(table, org.OrgId));
    }

    [Fact]
    public async Task The_neighbouring_organization_is_untouched()
    {
        var mine = await PopulatedOrgAsync();
        var theirs = await PopulatedOrgAsync();

        var before = await SnapshotAsync(theirs.OrgId);
        await PurgeAsync(mine.OrgId);
        var after = await SnapshotAsync(theirs.OrgId);

        // Une purge qui déborde est une perte de données, et elle ne se rattrape pas.
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task The_audit_log_of_the_purged_organization_goes_with_it()
    {
        var org = await PopulatedOrgAsync();

        // Le journal porte les identifiants des membres : le laisser reviendrait à conserver
        // précisément ce que l'effacement doit retirer.
        Assert.True(await CountForOrgAsync("audit_log", org.OrgId) > 0);

        await PurgeAsync(org.OrgId);

        Assert.Equal(0, await CountForOrgAsync("audit_log", org.OrgId));
    }

    /// <summary>Le garde-fou WORM est une dérogation, pas une désactivation.</summary>
    [Fact]
    public async Task The_append_only_guard_is_back_in_force_after_the_purge()
    {
        var org = await PopulatedOrgAsync();
        var survivor = await PopulatedOrgAsync();

        await PurgeAsync(org.OrgId);

        // Le trigger doit être rétabli : une purge qui laisserait le journal réécrivable ferait
        // bien plus de dégâts que celle qu'elle vient de faire. On l'exerce là où ça compte — sur
        // une AUTRE organisation, dont personne n'a demandé l'effacement.
        using var db = Connection();
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.ExecuteAsync(
            "DELETE FROM audit_log WHERE org_id = @OrgId", new { OrgId = survivor.OrgId }));

        Assert.Contains("append-only", error.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Purging_an_unknown_organization_is_a_no_op_and_says_so()
    {
        var report = await PurgeAsync(UlidGenerator.NewUlid());

        // Distinct d'une erreur : un script d'exploitation qui rejoue une purge déjà faite doit
        // pouvoir le constater sans s'arrêter.
        Assert.False(report.Found);
        Assert.Equal(0, report.TotalRows);
    }

    [Fact]
    public async Task Every_table_that_carries_organization_data_is_covered_by_the_purge()
    {
        // Le test qui protège l'avenir : une table ajoutée au schéma sans être ajoutée à la purge y
        // laisserait des données personnelles hors d'atteinte. La liste attendue est dérivée du
        // schéma réel, pas recopiée.
        using var db = Connection();
        var reachable = (await db.QueryAsync<string>("""
            SELECT DISTINCT table_name
            FROM information_schema.columns
            WHERE table_schema = 'public'
              AND column_name IN ('org_id', 'project_id', 'run_id', 'user_id', 'agent_id', 'trigger_id')
            """)).ToHashSet();

        // `organizations` n'a pas de colonne `org_id` (c'est elle), et `schema_migrations` ne porte
        // aucune donnée. `email_outbox` non plus : un courriel transactionnel n'appartient à aucun
        // locataire — il part vers une adresse qui n'a peut-être aucun compte — et ses lignes
        // disparaissent d'elles-mêmes à la remise.
        reachable.Remove("email_outbox");

        var purged = OrgPurgeService.PurgedTables.ToHashSet();
        purged.Remove("organizations");

        var uncovered = reachable.Except(purged).ToList();
        Assert.True(uncovered.Count == 0,
            $"Tables portant des données rattachées à une organisation mais absentes de la purge : " +
            $"{string.Join(", ", uncovered)}. Ajouter une étape dans OrgPurgeService.Steps.");
    }

    // ---- helpers ----

    private sealed record PopulatedOrg(string OrgId, string ProjectId, string AgentId);

    /// <summary>
    /// Une organisation avec de quoi purger : projet, agent publié, run, déclencheur, secret,
    /// utilisateur supplémentaire, et le journal d'audit que tout cela produit.
    /// </summary>
    private async Task<PopulatedOrg> PopulatedOrgAsync()
    {
        var (client, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
        using var _ = client;

        // Un second utilisateur : écrit dans `users` et dans `audit_log`.
        await TestData.CreateUserWithRoleAsync(client, UserRole.Developer);

        // Un run : écrit dans `runs`, `run_events` et `audit_log`.
        (await client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = agent.Id }))
            .EnsureSuccessStatusCode();

        // Un déclencheur : écrit dans `triggers` et `audit_log`.
        (await client.PostJsonAsync($"/api/projects/{project.Id}/triggers", new CreateTriggerRequest
        {
            AgentId = agent.Id, Name = "Purge", Type = "webhook", Provider = "github",
        })).EnsureSuccessStatusCode();

        // Le run part en tâche de fond et continue d'écrire des événements. La purge sait le gérer
        // — elle verrouille les lignes parentes — mais un test qui compare deux photographies doit
        // attendre que l'activité cesse, sinon il mesure le bruit de fond plutôt que la purge.
        await WaitForRunsToSettleAsync(auth.User.OrgId);

        return new PopulatedOrg(auth.User.OrgId, project.Id, agent.Id);
    }

    /// <summary>Attend qu'aucun run de l'organisation ne soit dans un état non terminal.</summary>
    private async Task WaitForRunsToSettleAsync(string orgId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            using var db = Connection();
            var running = await db.ExecuteScalarAsync<int>("""
                SELECT COUNT(*) FROM runs
                WHERE org_id = @OrgId
                  AND status NOT IN ('succeeded','failed','cancelled','timed_out','budget_exceeded','rejected','infra_error')
                """, new { OrgId = orgId });

            if (running == 0) return;
            await Task.Delay(100);
        }

        Assert.Fail($"Des runs de l'organisation {orgId} ne sont pas arrivés à un état terminal en 30 s.");
    }

    private async Task<OrgPurgeReport> PurgeAsync(string orgId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IOrgPurgeService>().PurgeAsync(orgId);
    }

    /// <summary>
    /// Compte les lignes d'une table rattachées à une organisation, quel que soit le chemin.
    ///
    /// Les tables ne portent pas toutes `org_id` : certaines passent par le projet, le run ou
    /// l'utilisateur. Le compte suit le même chemin que la purge — sinon il validerait autre chose
    /// que ce qu'elle fait.
    /// </summary>
    private async Task<int> CountForOrgAsync(string table, string orgId)
    {
        var sql = table switch
        {
            "organizations" => "SELECT COUNT(*) FROM organizations WHERE id = @OrgId",
            "run_events" or "approvals" or "artifacts" =>
                $"SELECT COUNT(*) FROM {table} WHERE run_id IN (SELECT id FROM runs WHERE org_id = @OrgId)",
            "project_memories" or "webhooks" =>
                $"SELECT COUNT(*) FROM {table} WHERE project_id IN (SELECT id FROM projects WHERE org_id = @OrgId)",
            "trigger_deliveries" =>
                "SELECT COUNT(*) FROM trigger_deliveries WHERE trigger_id IN (SELECT id FROM triggers WHERE org_id = @OrgId)",
            "mfa_recovery_codes" or "user_mfa" or "refresh_tokens" or "password_reset_tokens" =>
                $"SELECT COUNT(*) FROM {table} WHERE user_id IN (SELECT id FROM users WHERE org_id = @OrgId)",
            "agent_versions" =>
                "SELECT COUNT(*) FROM agent_versions WHERE agent_id IN (SELECT id FROM agents WHERE org_id = @OrgId)",
            _ => $"SELECT COUNT(*) FROM {table} WHERE org_id = @OrgId",
        };

        using var db = Connection();
        return await db.ExecuteScalarAsync<int>(sql, new { OrgId = orgId });
    }

    /// <summary>Le décompte de chaque table pour une organisation, pour comparer avant et après.</summary>
    private async Task<Dictionary<string, int>> SnapshotAsync(string orgId)
    {
        var snapshot = new Dictionary<string, int>();
        foreach (var table in OrgPurgeService.PurgedTables)
            snapshot[table] = await CountForOrgAsync(table, orgId);
        return snapshot;
    }

    private System.Data.IDbConnection Connection() =>
        _factory.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
}
