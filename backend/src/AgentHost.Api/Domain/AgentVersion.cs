namespace AgentHost.Api.Domain;

public class AgentVersion
{
    public string Id { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public int VersionNumber { get; set; }

    public string ManifestYaml { get; set; } = string.Empty;
    public string? ImageRef { get; set; }
    public string InputsSchema { get; set; } = "{}";
    public string? OutputsSchema { get; set; }

    public string DigestSha256 { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }
}
