using System.Text.Json.Nodes;

namespace AgentHost.Api.Domain;

public class Run
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public long Number { get; set; }

    public string AgentId { get; set; } = string.Empty;
    public string AgentVersionId { get; set; } = string.Empty;

    public RunStatus Status { get; set; } = RunStatus.Pending;

    public JsonNode Inputs { get; set; } = new JsonObject();
    public JsonNode Context { get; set; } = new JsonObject();
    public JsonNode? Outputs { get; set; }

    public string? WorkspacePath { get; set; }

    /// <summary>
    /// Base URL du tier runner qui détient le conteneur de ce run (colonne <c>runs.runner_url</c>,
    /// migration 0009). Écrite juste avant le lancement en mode <c>Runner:Mode = remote</c> ; c'est
    /// elle qui permet à n'importe quelle réplique du backend d'arrêter ou de lire un run qu'elle
    /// n'a pas lancé.
    ///
    /// <para><c>null</c> n'est pas une anomalie : run antérieur à la migration, run lancé en mode
    /// <c>inprocess</c> (le défaut), ou run qui n'a jamais atteint le lancement. Le mode distant
    /// répond alors explicitement « aucun runner enregistré » plutôt que de prétendre avoir agi.</para>
    /// </summary>
    public string? RunnerUrl { get; set; }

    public long? DurationMs { get; set; }
    public int? ExitCode { get; set; }

    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }

    public decimal? BudgetMaxUsd { get; set; }
    public decimal? BudgetUsedUsd { get; set; }

    public string? TriggeredByUserId { get; set; }
    public TriggeredByType TriggeredByType { get; set; } = TriggeredByType.Manual;

    public string? ParentRunId { get; set; }
    public string? RootRunId { get; set; }

    /// <summary>
    /// Profondeur dans l'arbre de chaînage : 0 pour un run lancé directement, parent + 1 sinon.
    ///
    /// Écrite à la création plutôt que recalculée : la seule chose qu'on en fait est de borner la
    /// récursion, et remonter la chaîne par une CTE récursive à chaque chaînage pour retrouver un
    /// nombre que le parent connaît déjà serait un coût pur.
    /// </summary>
    public int ChainDepth { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>
    /// Resource envelope resolved from the agent manifest at creation time. Not persisted as
    /// a column (the schema has no such field) — it is re-derived from the agent/manifest
    /// whenever a run needs to be (re)launched, and kept on the in-memory instance during
    /// the lifetime of a single ExecuteRunAsync flow.
    /// </summary>
    public RuntimeProfile RuntimeProfile { get; set; } = RuntimeProfile.Default();

    /// <summary>
    /// Short-lived, run-scoped callback credential minted just before launch and injected into the
    /// agent container as <c>AGENTHOST_RUN_TOKEN</c> (see docs/agent-protocol.md). Like
    /// <see cref="RuntimeProfile"/> it is *not* persisted — it exists only on the in-memory instance
    /// during a single launch flow, and is deliberately excluded from API responses so a run's
    /// credential never leaks to a human client.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? AgentRunToken { get; set; }
}
