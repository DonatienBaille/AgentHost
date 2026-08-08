using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Les métriques **métier** de la plateforme (feuille de route, lot 3).
///
/// L'instrumentation existante était purement technique : ASP.NET Core, HTTP sortant, runtime .NET,
/// Npgsql. Elle répond à « le service est-il en bonne santé », jamais à « combien m'ont coûté mes
/// agents, lesquels échouent, vais-je dépasser mon budget » — les questions pour lesquelles la
/// plateforme existe.
///
/// <b>OpenTelemetry reste le canal d'exposition.</b> Ces instruments sortent par `/metrics`
/// (Prometheus) et par OTLP comme le reste ; l'IHM en consommera un sous-ensemble agrégé servi par
/// l'API, sans qu'un système de métriques parallèle soit inventé.
///
/// <b>La cardinalité est le piège de ce fichier.</b> Une étiquette par run ferait exploser la série
/// temporelle — un run est un événement, pas une dimension. Les étiquettes retenues sont bornées
/// par la taille de l'installation : organisation, projet, agent, statut. Ni identifiant de run, ni
/// identifiant d'utilisateur, ni message d'erreur. L'attribution par utilisateur, demandée par le
/// lot 3, se fait par requête SQL sur `runs` (voir les endpoints d'agrégation), pas par étiquette.
/// </summary>
public class AgentHostMetrics
{
    /// <summary>Nom du Meter, à enregistrer dans la configuration OpenTelemetry.</summary>
    public const string MeterName = "AgentHost.Business";

    private readonly Histogram<double> _runDurationSeconds;
    private readonly Counter<long> _runsFinished;
    private readonly Counter<double> _runCostUsd;
    private readonly Histogram<double> _approvalWaitSeconds;
    private readonly Counter<long> _budgetExhausted;
    private readonly Counter<long> _infraErrors;
    private readonly UpDownCounter<long> _runsInFlight;

    public AgentHostMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);

        _runDurationSeconds = meter.CreateHistogram<double>(
            "agenthost.run.duration",
            unit: "s",
            description: "Durée d'un run, de son démarrage à son état terminal.");

        _runsFinished = meter.CreateCounter<long>(
            "agenthost.run.finished",
            unit: "{run}",
            description: "Runs arrivés dans un état terminal, ventilés par statut.");

        _runCostUsd = meter.CreateCounter<double>(
            "agenthost.run.cost",
            unit: "USD",
            description: "Coût cumulé des runs terminés.");

        _approvalWaitSeconds = meter.CreateHistogram<double>(
            "agenthost.approval.wait",
            unit: "s",
            description: "Temps écoulé entre la demande d'approbation et la décision.");

        _budgetExhausted = meter.CreateCounter<long>(
            "agenthost.budget.exhausted",
            unit: "{run}",
            description: "Runs interrompus parce que leur budget était épuisé.");

        _infraErrors = meter.CreateCounter<long>(
            "agenthost.infra.error",
            unit: "{run}",
            description: "Runs perdus sur une défaillance d'infrastructure, pas sur le code de l'agent.");

        // UpDownCounter et non Gauge : la profondeur de file est une quantité qui monte et descend
        // au fil des événements, et personne n'est en position de l'observer d'un coup — il n'y a
        // pas de « file » matérialisée à interroger, seulement des runs en attente en base.
        _runsInFlight = meter.CreateUpDownCounter<long>(
            "agenthost.run.in_flight",
            unit: "{run}",
            description: "Runs partis et pas encore terminés (profondeur de la file d'exécution).");
    }

    /// <summary>Un run vient d'être accepté et attend son tour.</summary>
    public void RunQueued(string orgId) =>
        _runsInFlight.Add(1, new KeyValuePair<string, object?>("org", orgId));

    /// <summary>
    /// Un run vient d'atteindre un état terminal. Enregistre d'un coup tout ce que l'on sait de
    /// lui, pour qu'aucun appelant n'ait à se souvenir d'appeler trois méthodes.
    /// </summary>
    /// <param name="wasQueued">
    /// Ce run avait-il réellement occupé la file, c'est-à-dire dépassé l'état <c>Pending</c> ?
    ///
    /// <b>Le paramètre existe parce que son absence rendait la jauge négative.</b> Un run refusé
    /// avant tout lancement passe directement de <c>Pending</c> à un état terminal : il n'a jamais
    /// été compté à l'entrée, et le décompter à la sortie faisait descendre
    /// <c>agenthost.run.in_flight</c> sous zéro — une profondeur de file négative dans tout
    /// tableau de bord d'exploitation. L'appelant est le seul à connaître l'état précédent ; on le
    /// lui demande plutôt que de le deviner.
    /// </param>
    public void RunFinished(Run run, RunStatus status, bool wasQueued = true)
    {
        var tags = new TagList
        {
            { "org", run.OrgId },
            { "project", run.ProjectId },
            { "agent", run.AgentId },
            { "status", status.ToDbString() },
        };

        _runsFinished.Add(1, tags);

        // Symétrique de RunQueued, et uniquement pour les runs qui y sont réellement passés.
        if (wasQueued) _runsInFlight.Add(-1, new KeyValuePair<string, object?>("org", run.OrgId));

        // Une durée n'existe que si le run a réellement démarré : un run rejeté avant lancement n'a
        // pas de durée de 0, il n'en a pas du tout, et l'enregistrer à 0 tirerait les percentiles
        // vers le bas en donnant l'illusion de runs très rapides.
        if (run.StartedAt is { } startedAt && run.FinishedAt is { } finishedAt)
        {
            var elapsed = (finishedAt - startedAt).TotalSeconds;
            if (elapsed >= 0) _runDurationSeconds.Record(elapsed, tags);
        }

        if (run.BudgetUsedUsd is { } cost && cost > 0)
        {
            _runCostUsd.Add((double)cost, tags);
        }

        switch (status)
        {
            case RunStatus.BudgetExceeded:
                _budgetExhausted.Add(1, tags);
                break;
            case RunStatus.InfraError:
                // Distingué des échecs ordinaires : un agent qui échoue fait son travail, une
                // infrastructure qui échoue est notre problème. Les mélanger rendrait le taux de
                // réussite inexploitable pour décider où intervenir.
                _infraErrors.Add(1, tags);
                break;
        }
    }

    /// <summary>Une porte d'approbation vient d'être décidée, après <paramref name="waited"/>.</summary>
    public void ApprovalDecided(string orgId, string projectId, TimeSpan waited, bool approved)
    {
        if (waited < TimeSpan.Zero) return;

        _approvalWaitSeconds.Record(waited.TotalSeconds, new TagList
        {
            { "org", orgId },
            { "project", projectId },
            { "decision", approved ? "approved" : "rejected" },
        });
    }
}
