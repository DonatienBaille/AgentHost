namespace AgentHost.Api.Domain;

public class Artifact
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty; // e.g. "fix.patch", "report.md"
    public string? ArtifactType { get; set; } // patch, report, data, logs

    public string S3Path { get; set; } = string.Empty; // s3://bucket/artifacts/{runId}/{name}
    public long? SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; }
}
