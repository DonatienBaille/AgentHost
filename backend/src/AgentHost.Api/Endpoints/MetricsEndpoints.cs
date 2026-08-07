using AgentHost.Api.Infrastructure;
using Dapper;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Agrégats métier servis à l'IHM (feuille de route, lot 3).
///
/// <b>Pourquoi ces endpoints existent alors qu'OpenTelemetry expose déjà tout.</b> Les instruments
/// OTEL sortent vers Prometheus ou un collecteur : ils répondent aux questions d'un exploitant qui
/// dispose d'un Grafana. Un utilisateur de la plateforme n'a rien de tout ça, et ses questions sont
/// différentes — « combien m'ont coûté MES agents ce mois-ci », « lequel échoue », « vais-je
/// dépasser MON budget ». Les réponses doivent être scopées à son organisation, ce qu'une série
/// Prometheus ne sait pas faire sans donner accès à celles des autres.
///
/// La source est donc `runs`, pas les compteurs : la table porte déjà tout l'historique, avec
/// l'attribution fine (utilisateur déclencheur, projet, agent) que les étiquettes OTEL évitent
/// délibérément pour ne pas exploser la cardinalité. Les deux canaux sont complémentaires, pas
/// redondants — et surtout, aucun système de métriques parallèle n'est inventé.
///
/// <b>Isolation.</b> Comme partout dans ce dépôt, le périmètre vient du JWT et jamais de la
/// requête : il n'y a aucun paramètre d'organisation à falsifier.
/// </summary>
public static class MetricsEndpoints
{
    /// <summary>Fenêtre par défaut, et plafond. 90 jours de runs suffisent à toute décision ici.</summary>
    private const int DefaultWindowDays = 30;
    private const int MaxWindowDays = 90;

    /// <summary>Combien d'entrées au maximum dans un classement. Un top 50 n'est plus un top.</summary>
    private const int MaxLeaders = 20;

    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/metrics").WithTags("Metrics").RequireAuthorization();

        api.MapGet("/overview", Overview).WithName("MetricsOverview");
        api.MapGet("/by-agent", ByAgent).WithName("MetricsByAgent");
        api.MapGet("/by-project", ByProject).WithName("MetricsByProject");
        api.MapGet("/daily", Daily).WithName("MetricsDaily");

        return app;
    }

    private static int ClampWindow(int days) =>
        days < 1 ? 1 : days > MaxWindowDays ? MaxWindowDays : days;

    /// <summary>
    /// Les chiffres de tête d'une organisation sur la fenêtre : volume, issue, coût, attente
    /// d'approbation.
    /// </summary>
    private static async Task<Ok<MetricsOverview>> Overview(
        IDbConnectionFactory connectionFactory,
        ICallerContext caller,
        CancellationToken ct,
        int days = DefaultWindowDays)
    {
        var window = ClampWindow(days);
        using var db = connectionFactory.CreateConnection();

        // Un seul aller-retour : ces compteurs sont lus ensemble, et les séparer ferait diverger
        // leurs fenêtres temporelles au fil des millisecondes.
        var row = await db.QuerySingleAsync<OverviewRow>(new CommandDefinition("""
            SELECT
                COUNT(*)                                                      AS total,
                COUNT(*) FILTER (WHERE status = 'succeeded')                  AS succeeded,
                COUNT(*) FILTER (WHERE status IN ('failed','rejected','timed_out','budget_exceeded','infra_error')) AS failed,
                COUNT(*) FILTER (WHERE status = 'infra_error')                AS infra_errors,
                COUNT(*) FILTER (WHERE status = 'budget_exceeded')            AS budget_exceeded,
                COUNT(*) FILTER (WHERE finished_at IS NULL)                   AS in_flight,
                COALESCE(SUM(budget_used_usd), 0)                             AS cost_usd,
                COALESCE(AVG(duration_ms) FILTER (WHERE duration_ms IS NOT NULL), 0) AS avg_duration_ms
            FROM runs
            WHERE org_id = @OrgId AND created_at >= NOW() - (@Days || ' days')::interval
            """, new { caller.OrgId, Days = window }, cancellationToken: ct));

        var pendingApprovalSeconds = await db.ExecuteScalarAsync<double?>(new CommandDefinition("""
            SELECT COALESCE(MAX(EXTRACT(EPOCH FROM (NOW() - a.created_at))), 0)
            FROM approvals a
            JOIN runs r ON r.id = a.run_id
            WHERE r.org_id = @OrgId AND a.status = 'pending'
            """, new { caller.OrgId }, cancellationToken: ct));

        return TypedResults.Ok(new MetricsOverview
        {
            WindowDays = window,
            TotalRuns = row.Total,
            SucceededRuns = row.Succeeded,
            FailedRuns = row.Failed,
            InfraErrorRuns = row.InfraErrors,
            BudgetExceededRuns = row.BudgetExceeded,
            InFlightRuns = row.InFlight,
            CostUsd = row.CostUsd,
            AverageDurationMs = (long)row.AvgDurationMs,
            // Pas une moyenne : ce qui inquiète, c'est la demande qui attend depuis le plus
            // longtemps. Une moyenne basse masquerait une approbation oubliée depuis trois jours.
            OldestPendingApprovalSeconds = (long)(pendingApprovalSeconds ?? 0),
        });
    }

    private static Task<Ok<List<AgentUsage>>> ByAgent(
        IDbConnectionFactory connectionFactory, ICallerContext caller, CancellationToken ct,
        int days = DefaultWindowDays) =>
        Leaderboard<AgentUsage>(connectionFactory, caller, ct, days, """
            SELECT r.agent_id AS AgentId,
                   COALESCE(MAX(a.name), r.agent_id)                          AS Name,
                   COUNT(*)                                                   AS Runs,
                   COUNT(*) FILTER (WHERE r.status = 'succeeded')             AS Succeeded,
                   COALESCE(SUM(r.budget_used_usd), 0)                        AS CostUsd,
                   COALESCE(AVG(r.duration_ms) FILTER (WHERE r.duration_ms IS NOT NULL), 0) AS AverageDurationMs
            FROM runs r
            LEFT JOIN agents a ON a.id = r.agent_id
            WHERE r.org_id = @OrgId AND r.created_at >= NOW() - (@Days || ' days')::interval
            GROUP BY r.agent_id
            ORDER BY COUNT(*) DESC
            LIMIT @Limit
            """);

    private static Task<Ok<List<ProjectUsage>>> ByProject(
        IDbConnectionFactory connectionFactory, ICallerContext caller, CancellationToken ct,
        int days = DefaultWindowDays) =>
        Leaderboard<ProjectUsage>(connectionFactory, caller, ct, days, """
            SELECT r.project_id AS ProjectId,
                   COALESCE(MAX(p.name), r.project_id)                        AS Name,
                   COUNT(*)                                                   AS Runs,
                   COUNT(*) FILTER (WHERE r.status = 'succeeded')             AS Succeeded,
                   COALESCE(SUM(r.budget_used_usd), 0)                        AS CostUsd,
                   MAX(p.budget_monthly_usd)                                  AS BudgetMonthlyUsd
            FROM runs r
            LEFT JOIN projects p ON p.id = r.project_id
            WHERE r.org_id = @OrgId AND r.created_at >= NOW() - (@Days || ' days')::interval
            GROUP BY r.project_id
            ORDER BY COALESCE(SUM(r.budget_used_usd), 0) DESC
            LIMIT @Limit
            """);

    /// <summary>Une ligne par jour, pour tracer une courbe plutôt qu'afficher un total.</summary>
    private static async Task<Ok<List<DailyPoint>>> Daily(
        IDbConnectionFactory connectionFactory, ICallerContext caller, CancellationToken ct,
        int days = DefaultWindowDays)
    {
        var window = ClampWindow(days);
        using var db = connectionFactory.CreateConnection();

        // generate_series et non GROUP BY seul : un jour sans run doit apparaître à zéro. Sans lui,
        // la courbe relierait les deux jours voisins et masquerait l'interruption.
        var rows = await db.QueryAsync<DailyPoint>(new CommandDefinition("""
            SELECT d.day::date                                                 AS Day,
                   COUNT(r.id)                                                 AS Runs,
                   COUNT(r.id) FILTER (WHERE r.status = 'succeeded')           AS Succeeded,
                   COALESCE(SUM(r.budget_used_usd), 0)                         AS CostUsd
            FROM generate_series(
                     date_trunc('day', NOW() - (@Days || ' days')::interval),
                     date_trunc('day', NOW()),
                     '1 day') AS d(day)
            LEFT JOIN runs r
                   ON r.org_id = @OrgId
                  AND date_trunc('day', r.created_at) = d.day
            GROUP BY d.day
            ORDER BY d.day
            """, new { caller.OrgId, Days = window }, cancellationToken: ct));

        return TypedResults.Ok(rows.ToList());
    }

    private static async Task<Ok<List<T>>> Leaderboard<T>(
        IDbConnectionFactory connectionFactory, ICallerContext caller, CancellationToken ct,
        int days, string sql)
    {
        using var db = connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<T>(new CommandDefinition(
            sql, new { caller.OrgId, Days = ClampWindow(days), Limit = MaxLeaders },
            cancellationToken: ct));
        return TypedResults.Ok(rows.ToList());
    }

    private sealed class OverviewRow
    {
        public int Total { get; set; }
        public int Succeeded { get; set; }
        public int Failed { get; set; }
        public int InfraErrors { get; set; }
        public int BudgetExceeded { get; set; }
        public int InFlight { get; set; }
        public decimal CostUsd { get; set; }
        public double AvgDurationMs { get; set; }
    }
}

public class MetricsOverview
{
    public int WindowDays { get; set; }
    public int TotalRuns { get; set; }
    public int SucceededRuns { get; set; }
    public int FailedRuns { get; set; }
    public int InfraErrorRuns { get; set; }
    public int BudgetExceededRuns { get; set; }
    public int InFlightRuns { get; set; }
    public decimal CostUsd { get; set; }
    public long AverageDurationMs { get; set; }

    /// <summary>
    /// L'attente de la plus ancienne approbation encore en suspens. Une moyenne masquerait une
    /// demande oubliée ; c'est justement celle-là qu'il faut voir.
    /// </summary>
    public long OldestPendingApprovalSeconds { get; set; }
}

public class AgentUsage
{
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Runs { get; set; }
    public int Succeeded { get; set; }
    public decimal CostUsd { get; set; }
    public double AverageDurationMs { get; set; }
}

public class ProjectUsage
{
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Runs { get; set; }
    public int Succeeded { get; set; }
    public decimal CostUsd { get; set; }

    /// <summary>Plafond mensuel du projet, pour situer le coût. Null = aucun plafond fixé.</summary>
    public decimal? BudgetMonthlyUsd { get; set; }
}

public class DailyPoint
{
    public DateTime Day { get; set; }
    public int Runs { get; set; }
    public int Succeeded { get; set; }
    public decimal CostUsd { get; set; }
}
