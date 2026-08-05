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
