using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Le planificateur des déclencheurs cron (feuille de route, lot 4).
///
/// <b>Ce qui se teste ici et nulle part ailleurs.</b> Le calcul des échéances est prouvé isolément
/// par <c>CronScheduleTests</c>. Ce qui reste — et qui est la partie dangereuse — c'est la
/// réservation : plusieurs répliques du backend voient la même échéance au même battement, et rien
/// dans le code C# ne les empêche de la lancer toutes les deux. Seule l'atomicité de l'UPDATE le
/// fait, et cela ne se vérifie que contre une vraie base.
///
/// Le second point est économique autant que technique : un backend arrêté trois jours ne doit pas,
/// au redémarrage, rejouer soixante-douze occurrences d'un agent horaire. La facture d'une panne
/// serait alors payée deux fois.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TriggerSchedulerTests
{
    private readonly AgentHostApiFactory _factory;

    public TriggerSchedulerTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task A_due_trigger_fires_once_and_is_rescheduled()
    {
        var f = await FixtureAsync();
        var trigger = await CreateCronTriggerAsync(f, "*/5 * * * *");
        await MakeDueAsync(trigger.Id, DateTime.UtcNow.AddMinutes(-1));

        // Les assertions portent sur CE déclencheur et non sur le compte global : le battement
        // sert toute l'installation, et la base de test est partagée entre les classes.
        await TickAsync();

        var reloaded = await ReloadAsync(trigger.Id, f.OrgId);
        Assert.NotNull(reloaded!.LastRunId);
        // Replacé dans le futur : sans cela le battement suivant le relancerait aussitôt, et le
        // suivant encore.
        Assert.True(reloaded.NextRunAt > DateTime.UtcNow);

        var run = await LoadRunAsync(reloaded.LastRunId!);
        Assert.Equal(TriggeredByType.Cron, run.TriggeredByType);
        // Personne n'a cliqué : l'acteur reste nul plutôt que d'emprunter l'identité de qui a
        // configuré la planification, il y a peut-être des mois.
        Assert.Null(run.TriggeredByUserId);
        Assert.Equal(trigger.Id, run.Context?["trigger"]?["id"]?.GetValue<string>());
        Assert.Equal("*/5 * * * *", run.Context?["trigger"]?["cron"]?.GetValue<string>());
    }

    [Fact]
    public async Task A_second_beat_does_not_fire_the_same_trigger_again()
    {
        var f = await FixtureAsync();
        var trigger = await CreateCronTriggerAsync(f, "0 3 * * *");
        await MakeDueAsync(trigger.Id, DateTime.UtcNow.AddMinutes(-1));

        await TickAsync();
        var afterFirst = await ReloadAsync(trigger.Id, f.OrgId);
        Assert.NotNull(afterFirst!.LastRunId);

        // La réservation a déjà déplacé l'échéance : le battement suivant ne la revoit pas, et le
        // run précédent reste le dernier.
        await TickAsync();
        Assert.Equal(afterFirst.LastRunId, (await ReloadAsync(trigger.Id, f.OrgId))!.LastRunId);
    }

    [Fact]
    public async Task A_trigger_that_is_not_due_yet_stays_put()
    {
        var f = await FixtureAsync();
        var trigger = await CreateCronTriggerAsync(f, "0 3 * * *");
        var scheduled = DateTime.UtcNow.AddHours(2);
        await MakeDueAsync(trigger.Id, scheduled);

        await TickAsync();

        var reloaded = await ReloadAsync(trigger.Id, f.OrgId);
        Assert.Null(reloaded!.LastRunId);
        Assert.Equal(scheduled, reloaded.NextRunAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_disabled_trigger_is_never_fired_even_when_its_schedule_says_so()
    {
        var f = await FixtureAsync();
        var trigger = await CreateCronTriggerAsync(f, "*/5 * * * *");

        // Échéance dans le passé ET déclencheur désactivé : le seul garde-fou est le prédicat
        // `is_active` de la requête, et c'est lui qu'on vérifie.
        await MakeDueAsync(trigger.Id, DateTime.UtcNow.AddMinutes(-1), active: false);

        await TickAsync();
        Assert.Null((await ReloadAsync(trigger.Id, f.OrgId))!.LastRunId);
    }

    /// <summary>Le test qui compte : deux répliques, une seule exécution.</summary>
    [Fact]
    public async Task Two_schedulers_racing_on_the_same_due_trigger_produce_one_claim()
    {
        var f = await FixtureAsync();
        var trigger = await CreateCronTriggerAsync(f, "*/5 * * * *");
        var due = DateTime.UtcNow.AddMinutes(-1);
        await MakeDueAsync(trigger.Id, due);

        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITriggerRepository>();

        // Les deux partent de la même échéance lue — exactement ce que deux répliques observent au
        // même battement.
        var claims = await Task.WhenAll(
            repository.ClaimDueAsync(trigger.Id, due, due.AddMinutes(5)),
            repository.ClaimDueAsync(trigger.Id, due, due.AddMinutes(5)));

        // Une seule réservation aboutit. Perdre la course est le cas normal, pas une anomalie :
        // la ligne elle-même est le jeton, il n'y a ni verrou distribué ni élection de chef.
        Assert.Single(claims.Where(c => c is not null));
    }

    [Fact]
    public async Task Missed_occurrences_are_not_replayed_after_an_outage()
    {
        var f = await FixtureAsync();
        var trigger = await CreateCronTriggerAsync(f, "0 * * * *"); // toutes les heures
        // Trois jours d'arrêt : soixante-douze occurrences manquées.
        await MakeDueAsync(trigger.Id, DateTime.UtcNow.AddDays(-3));

        await TickAsync();

        // Un seul run pour ce projet, et la prochaine échéance calculée à partir de maintenant.
        // Rejouer les soixante-douze ferait payer deux fois la facture d'une panne.
        Assert.Single(await ListRunsAsync(f));
        var reloaded = await ReloadAsync(trigger.Id, f.OrgId);
        Assert.True(reloaded!.NextRunAt > DateTime.UtcNow);
        Assert.True(reloaded.NextRunAt < DateTime.UtcNow.AddHours(1).AddMinutes(1));
    }

    [Fact]
    public async Task A_schedule_with_no_future_occurrence_is_unscheduled_rather_than_re_read_forever()
    {
        var f = await FixtureAsync();
        // Le 30 février : accepté à l'analyse, jamais atteint.
        var trigger = await CreateCronTriggerAsync(f, "0 0 30 2 *");
        await MakeDueAsync(trigger.Id, DateTime.UtcNow.AddMinutes(-1));

        await TickAsync();

        var reloaded = await ReloadAsync(trigger.Id, f.OrgId);
        // `next_run_at = NULL` sort la ligne de l'index des échéances ; la laisser dans le passé
        // la ferait relire à chaque battement, pour toujours.
        Assert.Null(reloaded!.NextRunAt);
        Assert.Null(reloaded.LastRunId);
    }

    [Fact]
    public async Task Another_organizations_due_trigger_is_fired_too_because_the_scheduler_serves_the_installation()
    {
        var mine = await FixtureAsync();
        var theirs = await FixtureAsync();
        var trigger = await CreateCronTriggerAsync(theirs, "*/5 * * * *");
        await MakeDueAsync(trigger.Id, DateTime.UtcNow.AddMinutes(-1));

        // Le planificateur est un service d'exploitation, pas une requête d'utilisateur : son
        // absence de périmètre est délibérée, et ce test l'énonce plutôt que de la laisser
        // ressembler à un oubli. Le run produit reste, lui, dans l'organisation du déclencheur.
        await TickAsync();

        var fired = await ReloadAsync(trigger.Id, theirs.OrgId);
        var run = await LoadRunAsync(fired!.LastRunId!);
        Assert.Equal(theirs.OrgId, run.OrgId);
        Assert.NotEqual(mine.OrgId, run.OrgId);
    }

    // ---- helpers ----

    private sealed record Fixture(HttpClient Client, string OrgId, Project Project, Agent Agent);

    private async Task<Fixture> FixtureAsync()
    {
        var (client, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
        return new Fixture(client, auth.User.OrgId, project, agent);
    }

    private async Task<TriggerResponse> CreateCronTriggerAsync(Fixture f, string cron)
    {
        var response = await f.Client.PostJsonAsync($"/api/projects/{f.Project.Id}/triggers", new CreateTriggerRequest
        {
            AgentId = f.Agent.Id,
            Name = $"Planifié {cron}",
            Type = "cron",
            CronExpression = cron,
        });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateTriggerResponse>(TestJson.Options);
        return created!.Trigger;
    }

    /// <summary>
    /// Place l'échéance où le test en a besoin, directement en SQL.
    ///
    /// Aucun chemin applicatif ne le permet — et c'est voulu : une échéance ne se choisit pas, elle
    /// se calcule. Le test, lui, a besoin d'un passé pour observer un réveil.
    /// </summary>
    private async Task MakeDueAsync(string triggerId, DateTime nextRunAt, bool active = true)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var db = factory.CreateConnection();
        await db.ExecuteAsync(
            "UPDATE triggers SET next_run_at = @NextRunAt, is_active = @Active WHERE id = @Id",
            new { Id = triggerId, NextRunAt = nextRunAt, Active = active });
    }

    /// <summary>
    /// Un battement, exécuté à la demande plutôt qu'attendu.
    ///
    /// Le service tourne bien dans l'application, mais l'attendre rendrait chaque test tributaire
    /// d'un délai de vingt secondes et de l'état laissé par le battement précédent.
    /// </summary>
    private async Task<int> TickAsync()
    {
        var scheduler = new TriggerScheduler(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            _factory.Services.GetRequiredService<ILogger>());
        return await scheduler.TickAsync(CancellationToken.None);
    }

    private async Task<Trigger?> ReloadAsync(string triggerId, string orgId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ITriggerRepository>().GetAsync(triggerId, orgId);
    }

    private async Task<List<Run>> ListRunsAsync(Fixture f)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRunRepository>()
            .ListByProjectAsync(f.Project.Id, f.OrgId, 0, 200);
    }

    private async Task<Run> LoadRunAsync(string runId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IRunRepository>().GetAsync(runId)
            ?? throw new InvalidOperationException($"Run {runId} not found");
    }
}
