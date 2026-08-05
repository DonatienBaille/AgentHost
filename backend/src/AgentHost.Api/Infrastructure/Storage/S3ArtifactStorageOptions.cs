namespace AgentHost.Api.Infrastructure.Storage;

/// <summary>
/// Configuration for <see cref="S3ArtifactStorage"/>, read from the <c>Artifacts:S3</c> section.
/// Pure data + validation, deliberately separate from the client so the parts that can be wrong at
/// deploy time are unit-testable without reaching an endpoint.
/// </summary>
public sealed class S3ArtifactStorageOptions
{
    public const string DefaultKeyPrefix = "artifacts";

    /// <summary>Bucket holding every artifact. Required.</summary>
    public string Bucket { get; init; } = string.Empty;

    /// <summary>
    /// Custom endpoint for an S3-compatible service (MinIO, Ceph RGW), e.g.
    /// <c>http://minio:9000</c>. Empty means real AWS S3, addressed through <see cref="Region"/>.
    /// </summary>
    public string? ServiceUrl { get; init; }

    /// <summary>AWS region. Required for real S3; defaults to <c>us-east-1</c> behind a custom endpoint.</summary>
    public string? Region { get; init; }

    /// <summary>Static access key. Leave both credentials empty to use the SDK's default chain (IRSA, instance profile, env).</summary>
    public string? AccessKey { get; init; }

    public string? SecretKey { get; init; }

    /// <summary>
    /// Path-style addressing (<c>host/bucket/key</c>). Required by MinIO and most Ceph deployments,
    /// which have no per-bucket DNS; defaults to true whenever <see cref="ServiceUrl"/> is set.
    /// </summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>Key namespace inside the bucket. Lets one bucket host several environments.</summary>
    public string KeyPrefix { get; init; } = DefaultKeyPrefix;

    public static S3ArtifactStorageOptions FromConfiguration(IConfiguration config)
    {
        var section = config.GetSection("Artifacts:S3");
        var serviceUrl = Trimmed(section["ServiceUrl"]);
        var prefix = Trimmed(section["KeyPrefix"]);

        return new S3ArtifactStorageOptions
        {
            Bucket = Trimmed(section["Bucket"]) ?? string.Empty,
            ServiceUrl = serviceUrl,
            Region = Trimmed(section["Region"]),
            AccessKey = Trimmed(section["AccessKey"]),
            SecretKey = Trimmed(section["SecretKey"]),
            // Explicit setting wins; otherwise path style exactly when a custom endpoint is used.
            ForcePathStyle = bool.TryParse(section["ForcePathStyle"], out var forced) ? forced : serviceUrl is not null,
            KeyPrefix = (prefix ?? DefaultKeyPrefix).Trim('/'),
        };
    }

    /// <summary>
    /// Everything that makes this configuration unusable, in one pass, so a misconfigured deployment
    /// fails at startup with the full list instead of on the first upload with one symptom.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Bucket))
            errors.Add("Artifacts:S3:Bucket is required when Artifacts:Provider is 's3'.");

        if (string.IsNullOrWhiteSpace(ServiceUrl) && string.IsNullOrWhiteSpace(Region))
            errors.Add("Artifacts:S3:Region is required when no Artifacts:S3:ServiceUrl is given (AWS S3 cannot be addressed without a region).");

        // Scheme-checked, not merely parseable: "minio:9000" is a valid absolute URI (scheme "minio")
        // that the SDK cannot use, and it is exactly the shape an operator writes by mistake.
        if (!string.IsNullOrWhiteSpace(ServiceUrl) &&
            !(Uri.TryCreate(ServiceUrl, UriKind.Absolute, out var uri) &&
              (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
        {
            errors.Add($"Artifacts:S3:ServiceUrl '{ServiceUrl}' must be an absolute http:// or https:// URL.");
        }

        // One half of a credential pair is always a mistake: the SDK would silently fall back to the
        // default chain and the deployment would fail with a confusing 403 instead of a config error.
        if (string.IsNullOrWhiteSpace(AccessKey) != string.IsNullOrWhiteSpace(SecretKey))
            errors.Add("Artifacts:S3:AccessKey and Artifacts:S3:SecretKey must be set together (or both left empty to use the ambient credential chain).");

        return errors;
    }

    /// <summary>True when static credentials were supplied; otherwise the SDK's default chain is used.</summary>
    public bool HasStaticCredentials =>
        !string.IsNullOrWhiteSpace(AccessKey) && !string.IsNullOrWhiteSpace(SecretKey);

    /// <summary>Region actually handed to the SDK. Behind a custom endpoint the value is a formality that still has to be present.</summary>
    public string EffectiveRegion => string.IsNullOrWhiteSpace(Region) ? "us-east-1" : Region!;

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
