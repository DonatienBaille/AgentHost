namespace AgentHost.Api.Infrastructure.Storage;

/// <summary>One stored object, as the retention sweep sees it.</summary>
/// <param name="Key">Value stored in <c>artifacts.s3_path</c> for this object.</param>
/// <param name="LastModifiedUtc">Last write time, used to apply <c>Retention:ArtifactDays</c>.</param>
public readonly record struct ArtifactObject(string Key, DateTime LastModifiedUtc);

/// <summary>
/// Where run artifacts live. Two implementations, chosen by <c>Artifacts:Provider</c>:
/// <see cref="LocalArtifactStorage"/> (<c>local</c>, the default and the historical behaviour) and
/// <see cref="S3ArtifactStorage"/> (<c>s3</c>, for AWS S3 / MinIO / Ceph RGW).
///
/// <para>The <c>artifacts.s3_path</c> column keeps whatever <see cref="DeriveKey"/> returns, and that
/// value stays meaningful per backend: an absolute filesystem path for local disk, an object key such
/// as <c>artifacts/{runId}/{artifactId}-{name}</c> for S3. No schema change, and rows written before
/// this abstraction existed keep resolving through the local implementation exactly as they did.</para>
/// </summary>
public interface IArtifactStorage
{
    /// <summary>Value of <c>Artifacts:Provider</c> this instance implements; logged at startup.</summary>
    string ProviderName { get; }

    /// <summary>
    /// Storage key for a new artifact, or null when the inputs cannot produce a safe one. Callers must
    /// pass an already-sanitized <paramref name="artifactName"/>; the implementation is still
    /// responsible for rejecting anything that would escape its own namespace.
    /// </summary>
    string? DeriveKey(string runId, string artifactId, string artifactName);

    /// <summary>Streams <paramref name="content"/> to <paramref name="key"/> and returns the bytes stored.</summary>
    Task<long> SaveAsync(string key, Stream content, string? contentType, CancellationToken ct);

    /// <summary>Opens the object for reading, or returns null when it does not exist.</summary>
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct);

    /// <summary>Deletes the object. Missing objects are not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>Enumerates every stored object, for the retention sweep.</summary>
    IAsyncEnumerable<ArtifactObject> ListAsync(CancellationToken ct);
}
