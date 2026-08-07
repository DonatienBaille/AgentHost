using System.Text.Json.Nodes;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

public class CreateRunRequest
{
    public string AgentId { get; set; } = string.Empty;
    public JsonNode? Inputs { get; set; }
    public JsonNode? Context { get; set; }
    public decimal? BudgetMaxUsd { get; set; }

    // NOTE: there is deliberately no TriggeredByUserId here. The actor is taken from the caller's
    // JWT (ICallerContext.UserId) — a client-supplied actor lets an attacker write an arbitrary
    // identity into runs.triggered_by_user_id and into the WORM audit log, which destroys the
    // evidentiary value of both. Same reasoning as dropping the redundant ProjectId (derived from
    // the agent) from this contract.

    public TriggeredByType TriggeredByType { get; set; } = TriggeredByType.Manual;
    public string? ParentRunId { get; set; }
}

public class ApprovalRequest
{
    public string? StepId { get; set; }
    public string Decision { get; set; } = "approve"; // approve | reject
    public string? Note { get; set; }

    // NOTE: no DecidedByUserId — see CreateRunRequest. An approval record whose "decided by" is
    // chosen by the requester is worse than no approval record at all.
}

public class AnswerQuestionRequest
{
    public string QuestionId { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
}

public class RunListQuery
{
    public int Skip { get; set; } = 0;
    public int Take { get; set; } = 50;
    public string? ProjectId { get; set; }
    public string? Status { get; set; }

    // No OrgId: runs are always listed within the caller's own organization.
}

/// <summary>
/// Ce qu'un agent en cours d'exécution demande pour en déclencher un autre (lot 4).
///
/// Il n'y a ni <c>parentRunId</c> ni <c>triggeredByType</c> : le parent est le run que le jeton
/// désigne, et le type est <c>chain</c> par construction. Les laisser passer par le corps
/// permettrait à un agent de déclarer un autre parent que le sien, donc de rattacher son enfant à
/// un arbre auquel il n'appartient pas — et d'en contourner les limites au passage.
/// </summary>
public class AgentChainRequest
{
    public string AgentId { get; set; } = string.Empty;
    public JsonNode? Inputs { get; set; }
    public JsonNode? Context { get; set; }
    public decimal? BudgetMaxUsd { get; set; }
}

public class AgentChainResponse
{
    public string RunId { get; set; } = string.Empty;
    public string RootRunId { get; set; } = string.Empty;
    public int ChainDepth { get; set; }
}

/// <summary>Un nœud de l'arbre de chaînage, servi à l'IHM.</summary>
public class RunTreeNode
{
    public string Id { get; set; } = string.Empty;
    public long Number { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string? AgentName { get; set; }
    public string Status { get; set; } = string.Empty;
    public string TriggeredByType { get; set; } = string.Empty;
    public string? ParentRunId { get; set; }
    public int ChainDepth { get; set; }
    public decimal? BudgetUsedUsd { get; set; }
    public long? DurationMs { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<RunTreeNode> Children { get; set; } = [];
}

/// <summary>
/// L'arbre complet auquel un run appartient, plus ses totaux.
///
/// Les totaux sont servis parce qu'ils sont la question qu'on se pose devant une cascade : ce que
/// l'ensemble a coûté, pas ce qu'a coûté le maillon qu'on regarde.
/// </summary>
public class RunTreeResponse
{
    public string RootRunId { get; set; } = string.Empty;
    public RunTreeNode? Root { get; set; }
    public int TotalRuns { get; set; }
    public decimal TotalBudgetUsedUsd { get; set; }
    public int MaxDepth { get; set; }
}
