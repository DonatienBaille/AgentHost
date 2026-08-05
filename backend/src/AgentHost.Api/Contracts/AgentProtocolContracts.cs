using System.Text.Json.Nodes;

namespace AgentHost.Api.Contracts;

// Request/response bodies for the agent → host callback protocol (/api/agent/..., version 1.0).
// See docs/agent-protocol.md. These are spoken by the agent *container*, authenticated with the
// run-scoped AGENTHOST_RUN_TOKEN — never by a human client.

public class AgentEventRequest
{
    /// <summary>Dotted event name, e.g. <c>phase.started</c>, <c>tool.called</c>, <c>log</c>.</summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>debug | info | warn | error. Defaults to info.</summary>
    public string? Level { get; set; }

    public string? Message { get; set; }

    public JsonNode? Payload { get; set; }
}

public class AgentEventResponse
{
    /// <summary>The monotonically increasing sequence number assigned to this event for the run.</summary>
    public long Seq { get; set; }
}

public class AgentOutputsRequest
{
    /// <summary>The agent's result document; persisted verbatim to <c>runs.outputs</c>.</summary>
    public JsonNode? Outputs { get; set; }
}

public class AgentApprovalRequest
{
    /// <summary>Human-readable question/gate text shown to the approver. Required.</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>Optional agent-side step identifier, echoed back on the decision.</summary>
    public string? StepId { get; set; }

    /// <summary>gate | question | budget_increase. Defaults to gate.</summary>
    public string? ApprovalType { get; set; }

    /// <summary>Optional list/object of allowed answers.</summary>
    public JsonNode? Options { get; set; }

    /// <summary>Minimum role allowed to decide. Defaults to the manifest's <c>approvals.beforeWrite.requiredRole</c>.</summary>
    public string? RequiredRole { get; set; }

    /// <summary>Number of approvals needed. Defaults to the manifest's <c>approvals.beforeWrite.requiredCount</c>, else 1.</summary>
    public int? RequiredCount { get; set; }

    /// <summary>How long the gate stays open before the watchdog expires it (default 3600s).</summary>
    public int? ExpiresInSeconds { get; set; }
}

public class AgentQuestionRequest
{
    public string Prompt { get; set; } = string.Empty;
    public JsonNode? Options { get; set; }
    public int? ExpiresInSeconds { get; set; }
}

public class AgentApprovalCreatedResponse
{
    public string ApprovalId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string RunStatus { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class AgentApprovalStatusResponse
{
    public string ApprovalId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // pending | approved | rejected | expired
    public string RunStatus { get; set; } = string.Empty;
    public string? Answer { get; set; }
    public string? Note { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public class AgentUsageRequest
{
    /// <summary>Incremental cost to add to <c>runs.budget_used_usd</c>. Must be &gt;= 0.</summary>
    public decimal CostUsd { get; set; }

    public long? TokensIn { get; set; }
    public long? TokensOut { get; set; }
    public string? Model { get; set; }
}

public class AgentUsageResponse
{
    public decimal BudgetUsedUsd { get; set; }
    public decimal? BudgetMaxUsd { get; set; }

    /// <summary>True when this report pushed the run over its budget — the run is now terminal.</summary>
    public bool BudgetExceeded { get; set; }
}
