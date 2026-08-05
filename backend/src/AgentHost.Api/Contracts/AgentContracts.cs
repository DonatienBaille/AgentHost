namespace AgentHost.Api.Contracts;

public class CreateAgentRequest
{
    public string OrgId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;

    /// <summary>Full agent manifest YAML (spec section 6.2). Parsed server-side via AgentManifestParser.</summary>
    public string ManifestYaml { get; set; } = string.Empty;

    public bool Publish { get; set; } = true;
}
