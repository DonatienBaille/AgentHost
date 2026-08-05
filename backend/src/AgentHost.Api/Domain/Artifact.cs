namespace AgentHost.Api.Domain;

public class Artifact
{
    public string Id { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty; // e.g. "fix.patch", "report.md"
    public string? ArtifactType { get; set; } // patch, report, data, logs

    // Despite the column name, this currently holds a local absolute filesystem path written by
    // ArtifactEndpoints (see Artifacts:StoragePath), not a real s3:// URI — an S3/blob storage
    // backend is future work.
    public string S3Path { get; set; } = string.Empty;
    public long? SizeBytes { get; set; }

    public DateTime CreatedAt { get; set; }
}
