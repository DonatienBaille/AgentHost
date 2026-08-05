using System.Text.Json.Nodes;

namespace AgentHost.Api.Domain;

/// <summary>Write-once, read-many audit trail entry (audit_log table).</summary>
public class AuditLogEntry
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty; // run.created, run.approved, secret.accessed, ...
    public string? ActorUserId { get; set; }

    public string? ResourceType { get; set; } // run, agent, secret, project
    public string? ResourceId { get; set; }

    public JsonNode? Changes { get; set; }
    public JsonNode? Details { get; set; }

    public DateTime CreatedAt { get; set; }
}
