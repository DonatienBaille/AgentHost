using System.Net.Http.Json;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Infrastructure.Storage;
using AgentHost.Api.Services;
using AgentHost.Shared.Containers;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// La purge des livraisons de webhook (feuille de route, lot 4).
///
/// <b>Pourquoi cette table est la seule à mériter une purge.</b> `trigger_deliveries` est la seule
/// du schéma dont la croissance n'est bornée par rien : ni par un nombre de runs, ni par un nombre
/// d'utilisateurs, seulement par le trafic entrant. Une ligne par livraison reçue, pour toujours.
///
/// <b>Ce que le test doit prouver, et dans cet ordre.</b> Qu'une entrée périmée disparaît — sinon
/// la purge ne sert à rien — mais surtout qu'une entrée <b>récente survit</b> : c'est elle qui
/// empêche une réémission de relancer l'agent, et une purge trop zélée rouvrirait exactement le
/// défaut que la déduplication existe pour fermer.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TriggerRetentionTests
{
    private readonly AgentHostApiFactory _factory;

    public TriggerRetentionTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_old_delivery_is_purged_and_a_recent_one_is_kept()
    {
        var triggerId = await SeedTriggerAsync();

        await SeedDeliveryAsync(triggerId, "ancienne", DateTime.UtcNow.AddDays(-45));
        await SeedDeliveryAsync(triggerId, "recente", DateTime.UtcNow.AddHours(-2));

        await SweepAsync(retentionDays: 30);

        Assert.False(await DeliveryExistsAsync(triggerId, "ancienne"));
        // La ligne récente est celle qui protège d'une réémission : la perdre ferait relancer
        // l'agent, et facturer deux fois le même événement.
        Assert.True(await DeliveryExistsAsync(triggerId, "recente"));
    }

    [Fact]
    public async Task A_retention_of_zero_disables_the_purge_entirely()
    {
        var triggerId = await SeedTriggerAsync();
        await SeedDeliveryAsync(triggerId, "tres-ancienne", DateTime.UtcNow.AddDays(-400));

        // Zéro = désactivé, comme pour la rétention d'artefacts. Le confondre avec « purger tout »
        // viderait la table à la première passe d'un déploiement qui n'a rien demandé.
        await SweepAsync(retentionDays: 0);

        Assert.True(await DeliveryExistsAsync(triggerId, "tres-ancienne"));
    }

    // ---- helpers ----

    /// <summary>
    /// Une passe de ménage avec la seule rétention qui nous intéresse.
    ///
    /// Les autres sont neutralisées (`ArtifactDays = 0`, `WorkspaceHours` très long) : ce test
    /// porte sur la purge des livraisons, et laisser le ménage disque agir en ferait un test de
    /// deux choses dont une seule est en cause quand il échoue.
    /// </summary>
    private async Task SweepAsync(int retentionDays)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Retention:TriggerDeliveryDays"] = retentionDays.ToString(),
                ["Retention:ArtifactDays"] = "0",
                ["Retention:WorkspaceHours"] = "100000",
                ["Retention:SecretsGraceMinutes"] = "100000",
            })
            .Build();

        using var scope = _factory.Services.CreateScope();
        var janitor = new RunDataJanitor(
            config,
            Serilog.Log.Logger,
            scope.ServiceProvider.GetRequiredService<IArtifactStorage>(),
            scope.ServiceProvider.GetRequiredService<ContainerPathMapper>(),
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>());

        await janitor.SweepAsync(CancellationToken.None);
    }

    /// <summary>Un déclencheur réel : `trigger_deliveries` porte une clé étrangère vers lui.</summary>
    private async Task<string> SeedTriggerAsync()
    {
        var (client, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
        using var _ = client;

        var response = await client.PostJsonAsync($"/api/projects/{project.Id}/triggers", new Api.Contracts.CreateTriggerRequest
        {
            AgentId = agent.Id,
            Name = "Rétention",
            Type = "webhook",
            Provider = "github",
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<Api.Contracts.CreateTriggerResponse>(TestJson.Options);
        Assert.NotNull(auth);
        return created!.Trigger.Id;
    }

    private async Task SeedDeliveryAsync(string triggerId, string deliveryId, DateTime receivedAt)
    {
        using var db = Connection();
        await db.ExecuteAsync(
            "INSERT INTO trigger_deliveries (trigger_id, delivery_id, received_at) VALUES (@T, @D, @R)",
            new { T = triggerId, D = deliveryId, R = receivedAt });
    }

    private async Task<bool> DeliveryExistsAsync(string triggerId, string deliveryId)
    {
        using var db = Connection();
        return await db.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM trigger_deliveries WHERE trigger_id = @T AND delivery_id = @D",
            new { T = triggerId, D = deliveryId }) > 0;
    }

    private System.Data.IDbConnection Connection() =>
        _factory.Services.GetRequiredService<IDbConnectionFactory>().CreateConnection();
}
