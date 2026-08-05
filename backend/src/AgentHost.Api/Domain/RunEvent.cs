using System.Text.Json.Nodes;

namespace AgentHost.Api.Domain;

/// <summary>Append-only run event (run_events table). Composite key (RunId, Seq).</summary>
public class RunEvent
{
    public string RunId { get; set; } = string.Empty;
    public long Seq { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public string EventType { get; set; } = string.Empty; // phase.started, tool.called, log, approval.requested, ...
    public string Level { get; set; } = "info"; // debug, info, warn, error

    public string? Message { get; set; }

    /// <summary>
    /// Event-specific payload. Accepts any serializable value (anonymous objects, JsonNode,
    /// dictionaries, ...) when publishing; round-trips through the DB as a JsonNode.
    /// </summary>
    public object? Payload { get; set; }
}
