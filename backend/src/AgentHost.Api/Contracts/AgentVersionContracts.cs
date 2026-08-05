using System.Text.Json.Nodes;

namespace AgentHost.Api.Contracts;

public class PublishAgentVersionRequest
{
    /// <summary>Full agent manifest YAML (spec section 6.2) for this version snapshot.</summary>
    public string ManifestYaml { get; set; } = string.Empty;

    public string? ImageRef { get; set; }
    public JsonNode? InputsSchema { get; set; }
    public JsonNode? OutputsSchema { get; set; }
}
