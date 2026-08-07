using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Endpoints;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Les agrégats métier servis à l'IHM (lot 3).
///
/// Du SQL ne se vérifie pas à la lecture : un `FILTER` mal placé, une jointure qui multiplie les
/// lignes ou une fenêtre temporelle décalée produisent des nombres parfaitement plausibles et faux.
/// Ces tests posent donc des runs dont on connaît la réponse attendue, puis la comparent.
///
/// Le test le plus important est celui de l'isolation : ces endpoints répondent « combien m'ont
/// coûté MES agents », et une fuite y serait une fuite de données d'exploitation d'un concurrent.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MetricsEndpointsTests
{
    private readonly AgentHostApiFactory _factory;

    public MetricsEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task The_overview_counts_each_outcome_in_its_own_bucket()
    {
        var f = await FixtureAsync();

        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 1.50m, durationMs: 1000);
        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 2.50m, durationMs: 3000);
        await SeedRunAsync(f, RunStatus.Failed, costUsd: 0.25m, durationMs: 500);
        await SeedRunAsync(f, RunStatus.InfraError, costUsd: 0m, durationMs: null);
        await SeedRunAsync(f, RunStatus.BudgetExceeded, costUsd: 10m, durationMs: 2000);

        var overview = await GetAsync<MetricsOverview>(f.Client, "/api/metrics/overview");

        Assert.Equal(5, overview.TotalRuns);
        Assert.Equal(2, overview.SucceededRuns);
        // `failed` est la famille entière des issues non réussies, infra et budget compris — sinon
        // le taux de réussite affiché ne totaliserait pas 100 %.
        Assert.Equal(3, overview.FailedRuns);
        Assert.Equal(1, overview.InfraErrorRuns);
        Assert.Equal(1, overview.BudgetExceededRuns);
        Assert.Equal(14.25m, overview.CostUsd);
    }

    [Fact]
    public async Task The_average_duration_ignores_runs_that_never_ran()
    {
        var f = await FixtureAsync();

        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 0m, durationMs: 1000);
        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 0m, durationMs: 3000);
        // Un run sans durée n'a pas duré zéro : le compter tirerait la moyenne vers le bas et
        // donnerait l'illusion d'agents plus rapides qu'ils ne sont.
        await SeedRunAsync(f, RunStatus.Rejected, costUsd: 0m, durationMs: null);

        var overview = await GetAsync<MetricsOverview>(f.Client, "/api/metrics/overview");

        Assert.Equal(2000, overview.AverageDurationMs);
    }

    [Fact]
    public async Task Runs_outside_the_window_are_not_counted()
    {
        var f = await FixtureAsync();

        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 1m, durationMs: 100);
        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 99m, durationMs: 100, ageDays: 45);

        var month = await GetAsync<MetricsOverview>(f.Client, "/api/metrics/overview?days=30");
        Assert.Equal(1, month.TotalRuns);
        Assert.Equal(1m, month.CostUsd);

        var quarter = await GetAsync<MetricsOverview>(f.Client, "/api/metrics/overview?days=90");
        Assert.Equal(2, quarter.TotalRuns);
        Assert.Equal(100m, quarter.CostUsd);
    }

    [Fact]
    public async Task The_window_is_clamped_rather_than_trusted()
    {
        var f = await FixtureAsync();
        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 1m, durationMs: 100);

        // Une fenêtre absurde ne doit ni faire échouer la requête ni déclencher un balayage complet.
        Assert.Equal(90, (await GetAsync<MetricsOverview>(f.Client, "/api/metrics/overview?days=9999")).WindowDays);
        Assert.Equal(1, (await GetAsync<MetricsOverview>(f.Client, "/api/metrics/overview?days=-5")).WindowDays);
    }

    /// <summary>Le test qui compte : ces chiffres sont des données d'exploitation.</summary>
    [Fact]
    public async Task Another_organizations_runs_are_invisible()
    {
        var mine = await FixtureAsync();
        var theirs = await FixtureAsync();

        await SeedRunAsync(theirs, RunStatus.Succeeded, costUsd: 42m, durationMs: 5000);
        await SeedRunAsync(theirs, RunStatus.Succeeded, costUsd: 42m, durationMs: 5000);

        var overview = await GetAsync<MetricsOverview>(mine.Client, "/api/metrics/overview");

        Assert.Equal(0, overview.TotalRuns);
        Assert.Equal(0m, overview.CostUsd);
        Assert.Empty(await GetAsync<List<AgentUsage>>(mine.Client, "/api/metrics/by-agent"));
        Assert.Empty(await GetAsync<List<ProjectUsage>>(mine.Client, "/api/metrics/by-project"));
    }

    [Fact]
    public async Task Anonymous_callers_get_nothing()
    {
        var anonymous = _factory.CreateClient();

        foreach (var path in new[] { "/api/metrics/overview", "/api/metrics/by-agent",
                                     "/api/metrics/by-project", "/api/metrics/daily" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task Per_agent_usage_carries_the_agent_name_and_its_own_totals()
    {
        var f = await FixtureAsync();
        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 3m, durationMs: 2000);
        await SeedRunAsync(f, RunStatus.Failed, costUsd: 1m, durationMs: 4000);

        var usage = Assert.Single(await GetAsync<List<AgentUsage>>(f.Client, "/api/metrics/by-agent"));

        Assert.Equal(f.Agent.Id, usage.AgentId);
        // La jointure sur `agents` doit rendre le nom, pas répéter l'identifiant, et surtout ne pas
        // multiplier les lignes : deux runs, une seule entrée.
        Assert.Equal(f.Agent.Name, usage.Name);
        Assert.Equal(2, usage.Runs);
        Assert.Equal(1, usage.Succeeded);
        Assert.Equal(4m, usage.CostUsd);
        Assert.Equal(3000, usage.AverageDurationMs);
    }

    [Fact]
    public async Task Per_project_usage_carries_the_monthly_budget_so_the_cost_can_be_situated()
    {
        var f = await FixtureAsync();
        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 7m, durationMs: 1000);

        var usage = Assert.Single(await GetAsync<List<ProjectUsage>>(f.Client, "/api/metrics/by-project"));

        Assert.Equal(f.Project.Id, usage.ProjectId);
        Assert.Equal(7m, usage.CostUsd);
        // Sans le plafond, un coût est un nombre sans signification.
        Assert.NotNull(usage.BudgetMonthlyUsd);
    }

    [Fact]
    public async Task The_daily_series_has_one_point_per_day_including_the_empty_ones()
    {
        var f = await FixtureAsync();
        await SeedRunAsync(f, RunStatus.Succeeded, costUsd: 2m, durationMs: 1000);

        var series = await GetAsync<List<DailyPoint>>(f.Client, "/api/metrics/daily?days=6");

        // Sept points pour six jours : les deux bornes sont incluses. Un jour sans run doit
        // apparaître à zéro, sinon la courbe relierait ses voisins et masquerait l'interruption.
        Assert.Equal(7, series.Count);
        Assert.Equal(series.OrderBy(p => p.Day).Select(p => p.Day), series.Select(p => p.Day));
        Assert.Equal(1, series.Sum(p => p.Runs));
        Assert.Equal(2m, series.Sum(p => p.CostUsd));
        Assert.Contains(series, p => p.Runs == 0);
    }

    [Fact]
    public async Task The_oldest_pending_approval_is_reported_not_the_average()
    {
        var f = await FixtureAsync();
        var run = await SeedRunAsync(f, RunStatus.AwaitingApproval, costUsd: 0m, durationMs: null);

        await SeedApprovalAsync(run, ApprovalStatus.Pending, ageHours: 3);
        await SeedApprovalAsync(run, ApprovalStatus.Pending, ageHours: 0);
        // Une demande déjà décidée n'attend plus : la compter ferait paniquer pour rien.
        await SeedApprovalAsync(run, ApprovalStatus.Approved, ageHours: 100);

        var overview = await GetAsync<MetricsOverview>(f.Client, "/api/metrics/overview");

        // ~3 h, et surtout pas la moyenne des deux en attente (~1 h 30), qui masquerait celle qui
        // traîne — c'est justement celle-là qu'il faut voir.
        Assert.InRange(overview.OldestPendingApprovalSeconds, 3 * 3600 - 120, 3 * 3600 + 120);
    }

    // ---- helpers ----

    private sealed record Fixture(HttpClient Client, AuthResponse Auth, Project Project, Agent Agent);

    private async Task<Fixture> FixtureAsync()
    {
        var (client, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
        return new Fixture(client, auth, project, agent);
    }

    private static async Task<T> GetAsync<T>(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>(TestJson.Options);
        Assert.NotNull(value);
        return value!;
    }

    /// <summary>
    /// Écrit un run directement par le dépôt : le but est de contrôler son statut, son coût, sa
    /// durée et son âge, ce que le lancement réel ne permet pas — il n'y a pas de démon ici, et un
    /// run lancé finirait en erreur d'infrastructure quoi qu'on veuille observer.
    /// </summary>
    private async Task<Run> SeedRunAsync(
        Fixture f, RunStatus status, decimal costUsd, long? durationMs, int ageDays = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();

        var createdAt = DateTime.UtcNow.AddDays(-ageDays);
        var run = new Run
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = f.Auth.User.OrgId,
            ProjectId = f.Project.Id,
            AgentId = f.Agent.Id,
            AgentVersionId = f.Agent.CurrentVersionId ?? f.Agent.Id,
            Number = Random.Shared.Next(1, 1_000_000),
            Status = status,
            TriggeredByType = TriggeredByType.Manual,
            TriggeredByUserId = f.Auth.User.Id,
            BudgetUsedUsd = costUsd,
            DurationMs = durationMs,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            StartedAt = durationMs is null ? null : createdAt,
            FinishedAt = durationMs is null ? null : createdAt.AddMilliseconds(durationMs.Value),
        };

        await runs.InsertAsync(run);
        await runs.UpdateAsync(run);
        return run;
    }

    private async Task SeedApprovalAsync(Run run, ApprovalStatus status, int ageHours)
    {
        using var scope = _factory.Services.CreateScope();
        var approvals = scope.ServiceProvider.GetRequiredService<IApprovalRepository>();

        await approvals.InsertAsync(new Approval
        {
            Id = UlidGenerator.NewUlid(),
            RunId = run.Id,
            StepId = "step-1",
            ApprovalType = ApprovalType.Gate,
            Prompt = "May I?",
            RequiredRole = "maintainer",
            RequiredCount = 1,
            Status = status,
            CreatedAt = DateTime.UtcNow.AddHours(-ageHours),
            ExpiresAt = DateTime.UtcNow.AddDays(1),
        });
    }
}
