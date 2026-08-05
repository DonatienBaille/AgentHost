namespace AgentHost.Api.Contracts;

public class CreateAgentRequest
{
    // No OrgId: derived from the caller's JWT, and the ProjectId below is verified to belong to it.
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    /// <summary>Full agent manifest YAML (spec section 6.2). Parsed server-side via AgentManifestParser.</summary>
    public string ManifestYaml { get; set; } = string.Empty;

    public bool Publish { get; set; } = true;
}

public class UpdateAgentRequest
{
    // Name only — manifest/schema changes must go through POST /api/agents/{id}/versions
    // (AgentVersionEndpoints), which snapshots a new immutable version.
    public string? Name { get; set; }
}
