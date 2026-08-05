using System.Runtime.CompilerServices;

namespace AgentHost.Api.Infrastructure.Storage;

/// <summary>
/// Artifacts on the API node's own filesystem, under <c>Artifacts:StoragePath</c>. Default provider;
/// keys are absolute paths, identical to what the endpoint used to write directly, so existing
/// <c>artifacts.s3_path</c> rows keep working unchanged.
///
/// <para>Every key is checked to resolve inside the run's own directory, which is itself checked to
/// resolve inside the storage root: a name that survives sanitizing but still contains traversal
/// (or an absurd run id) yields null rather than a path outside the root.</para>
/// </summary>
public sealed class LocalArtifactStorage : IArtifactStorage
{
    public const string DefaultStoragePath = "/var/agenthost/artifacts";

    private readonly string _root;

    public LocalArtifactStorage(string? storagePath)
    {
        var path = string.IsNullOrWhiteSpace(storagePath) ? DefaultStoragePath : storagePath.Trim();
        _root = Path.GetFullPath(path);
    }

    public LocalArtifactStorage(IConfiguration config) : this(config["Artifacts:StoragePath"]) { }

    public string ProviderName => "local";

    /// <summary>Absolute path of the storage root; exposed for the retention sweep and for tests.</summary>
    public string Root => _root;

    public string? DeriveKey(string runId, string artifactId, string artifactName)
    {
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(artifactId) || string.IsNullOrWhiteSpace(artifactName))
            return null;

        var runDir = Path.GetFullPath(Path.Combine(_root, runId));
        if (!IsUnder(_root, runDir))
            return null;

        var filePath = Path.GetFullPath(Path.Combine(runDir, $"{artifactId}-{artifactName}"));
        return IsUnder(runDir, filePath) ? filePath : null;
    }

    public async Task<long> SaveAsync(string key, Stream content, string? contentType, CancellationToken ct)
    {
        if (!IsUnder(_root, Path.GetFullPath(key)))
            throw new ArgumentException($"Artifact key '{key}' is outside the storage root.", nameof(key));

        Directory.CreateDirectory(Path.GetDirectoryName(key)!);

        await using (var file = File.Create(key))
        {
            await content.CopyToAsync(file, ct);
        }

        return new FileInfo(key).Length;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct)
    {
        // Keys come from the database, but a row written by an older/other configuration could point
        // anywhere; refuse to serve anything outside the configured root.
        if (string.IsNullOrWhiteSpace(key) || !IsUnder(_root, Path.GetFullPath(key)) || !File.Exists(key))
            return Task.FromResult<Stream?>(null);

        return Task.FromResult<Stream?>(File.OpenRead(key));
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(key) && IsUnder(_root, Path.GetFullPath(key)) && File.Exists(key))
            File.Delete(key);

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ArtifactObject> ListAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask; // no I/O to await; the enumeration itself is synchronous.

        if (!Directory.Exists(_root))
            yield break;

        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            yield return new ArtifactObject(file, File.GetLastWriteTimeUtc(file));
        }
    }

    private static bool IsUnder(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }
}
