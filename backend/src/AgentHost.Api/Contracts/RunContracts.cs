using System.Text.Json.Nodes;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

public class CreateRunRequest
{
    public string ProjectId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public JsonNode? Inputs { get; set; }
    public JsonNode? Context { get; set; }
    public decimal? BudgetMaxUsd { get; set; }
    public string? TriggeredByUserId { get; set; }
    public TriggeredByType TriggeredByType { get; set; } = TriggeredByType.Manual;
    public string? ParentRunId { get; set; }
}

public class ApprovalRequest
{
    public string? StepId { get; set; }
    public string Decision { get; set; } = "approve"; // approve | reject
    public string? Note { get; set; }
    public string? DecidedByUserId { get; set; }
}

public class AnswerQuestionRequest
{
    public string QuestionId { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string? AnsweredByUserId { get; set; }
}

public class RunListQuery
{
    public int Skip { get; set; } = 0;
    public int Take { get; set; } = 50;
    public string? ProjectId { get; set; }
    public string? OrgId { get; set; }
    public string? Status { get; set; }
}
