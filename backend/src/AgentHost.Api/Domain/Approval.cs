using System.Text.Json.Nodes;

namespace AgentHost.Api.Domain;

public class Approval
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string? StepId { get; set; }

    public ApprovalType ApprovalType { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public JsonNode? Options { get; set; } // possible answers

    public string? RequiredRole { get; set; } // developer, maintainer, owner
    public int RequiredCount { get; set; } = 1;

    public List<ApprovalResponse> Responses { get; set; } = new();

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;
    public DateTime ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class ApprovalResponse
{
    public string By { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty; // approve, reject, answer
    public DateTime At { get; set; }
    public string? Note { get; set; }
    public string? Answer { get; set; }
}
