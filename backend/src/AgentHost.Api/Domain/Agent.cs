namespace AgentHost.Api.Domain;

public class Agent
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    public AgentType AgentType { get; set; }
    public string? ImageRef { get; set; }

    public string ManifestYaml { get; set; } = string.Empty;

    public string InputsSchema { get; set; } = "{}"; // raw JSON Schema text
    public string? OutputsSchema { get; set; }

    /// <summary>Nullable until the first version is published (agents.current_version_id has no FK NOT NULL).</summary>
    public string? CurrentVersionId { get; set; }

    public bool IsPublished { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>Convenience accessor, not persisted directly (parsed from ManifestYaml permissions.secrets).</summary>
    public Dictionary<string, string>? ExternalConfig { get; set; }

    /// <summary>Convenience alias used by ContainerOrchestrator (spec 7.1 references agent.Type).</summary>
    public AgentType Type => AgentType;
}
