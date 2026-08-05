using System.Text;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Run artifact upload/download. Files are written to local disk under
/// <c>Artifacts:StoragePath</c> (default "/var/agenthost/artifacts"); the <c>artifacts.s3_path</c>
/// column currently stores that local absolute path, not a real s3:// URI — an S3/blob storage
/// backend is future work, this is a local-disk stand-in for it.
///
/// Every route resolves the artifact (or its run) with the caller's org from the JWT, so one
/// tenant cannot read, list or write into another tenant's run directory.
/// </summary>
public static class ArtifactEndpoints
{
    private const string DefaultStoragePath = "/var/agenthost/artifacts";

    /// <summary>Upper bound on the stored filename, leaving room for the "{ulid}-" prefix.</summary>
    private const int MaxArtifactNameLength = 150;

    public static IEndpointRouteBuilder MapArtifactEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/runs/{runId}/artifacts", UploadArtifact)
            .WithName("UploadArtifact").WithTags("Artifacts")
            .RequireAuthorization(AuthorizationPolicies.Developer)
            .DisableAntiforgery();

        app.MapGet("/api/runs/{runId}/artifacts", ListArtifactsForRun)
            .WithName("ListArtifactsForRun").WithTags("Artifacts").RequireAuthorization();

        var artifactsApi = app.MapGroup("/api/artifacts").WithTags("Artifacts").RequireAuthorization();
        artifactsApi.MapGet("/{id}", GetArtifact).WithName("GetArtifact");
        artifactsApi.MapGet("/{id}/download", DownloadArtifact).WithName("DownloadArtifact");

        return app;
    }

    private static async Task<IResult> UploadArtifact(
        string runId,
        HttpRequest request,
        IRunRepository runRepository,
        IArtifactRepository artifactRepository,
        IConfiguration configuration,
        ICallerContext caller,
        CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data body required" });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"];
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "'file' is required" });

        var name = form["name"].FirstOrDefault();
        var artifactType = form["artifactType"].FirstOrDefault();

        var run = await runRepository.GetAsync(runId, caller.OrgId, ct);
        if (run is null) return Results.NotFound();

        var storagePath = configuration["Artifacts:StoragePath"];
        if (string.IsNullOrWhiteSpace(storagePath)) storagePath = DefaultStoragePath;

        var requestedName = string.IsNullOrWhiteSpace(name) ? file.FileName : name;
        var artifactName = SanitizeArtifactName(requestedName);
        if (artifactName is null)
            return Results.BadRequest(new { error = "Artifact name must contain at least one letter, digit, '.', '-' or '_'" });

        var artifactId = UlidGenerator.NewUlid();

        // runId comes from the route, so it is sanitized too — a run id is always a ULID, but the
        // path is built from it regardless of whether the lookup above happened to be strict.
        var runDir = Path.GetFullPath(Path.Combine(storagePath, SanitizeArtifactName(runId) ?? artifactId));
        var storageRoot = Path.GetFullPath(storagePath);
        if (!IsUnder(storageRoot, runDir)) return Results.BadRequest(new { error = "Invalid run id" });

        Directory.CreateDirectory(runDir);

        var filePath = Path.GetFullPath(Path.Combine(runDir, $"{artifactId}-{artifactName}"));

        // Belt and braces: even after sanitizing, confirm the resolved path really is inside the
        // run's own directory before opening it for writing. Previously nothing stopped a name
        // like "../../etc/cron.d/x" from escaping; it only failed because the "{ulid}-" prefix
        // happened to make the first path segment nonexistent — an accident, not a defense.
        if (!IsUnder(runDir, filePath)) return Results.BadRequest(new { error = "Invalid artifact name" });

        await using (var fileStream = File.Create(filePath))
        {
            await file.CopyToAsync(fileStream, ct);
        }

        var artifact = new Artifact
        {
            Id = artifactId,
            RunId = runId,
            Name = artifactName,
            ArtifactType = artifactType,
            S3Path = filePath,
            SizeBytes = new FileInfo(filePath).Length,
            CreatedAt = DateTime.UtcNow,
        };

        await artifactRepository.InsertAsync(artifact, ct);
        return Results.Created($"/api/artifacts/{artifact.Id}", artifact);
    }

    private static async Task<IResult> ListArtifactsForRun(
        string runId, IArtifactRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var artifacts = await repository.ListByRunAsync(runId, caller.OrgId, ct);
        return Results.Ok(artifacts);
    }

    private static async Task<IResult> GetArtifact(string id, IArtifactRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var artifact = await repository.GetAsync(id, caller.OrgId, ct);
        return artifact != null ? Results.Ok(artifact) : Results.NotFound();
    }

    private static async Task<IResult> DownloadArtifact(string id, IArtifactRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var artifact = await repository.GetAsync(id, caller.OrgId, ct);
        if (artifact is null || !File.Exists(artifact.S3Path)) return Results.NotFound();

        var stream = File.OpenRead(artifact.S3Path);
        return Results.File(stream, "application/octet-stream", fileDownloadName: artifact.Name);
    }

    /// <summary>
    /// Reduces a client-supplied artifact name to a single safe path segment: strips any directory
    /// component (including Windows-style separators and drive-relative forms), then keeps only a
    /// conservative charset of letters, digits, '.', '-' and '_'. Returns null when nothing usable
    /// survives, or when the result would still be a traversal token ("." / "..").
    /// </summary>
    internal static string? SanitizeArtifactName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        // Normalize backslashes first: on Linux, Path.GetFileName does not treat '\' as a
        // separator, so "..\\..\\etc\\passwd" would otherwise pass through intact.
        var candidate = rawName.Replace('\\', '/');
        candidate = candidate[(candidate.LastIndexOf('/') + 1)..];
        candidate = Path.GetFileName(candidate);

        var builder = new StringBuilder(candidate.Length);
        foreach (var c in candidate)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')
                builder.Append(c);
        }

        var sanitized = builder.ToString().Trim('.');
        if (sanitized.Length == 0) return null;
        if (sanitized.Length > MaxArtifactNameLength) sanitized = sanitized[..MaxArtifactNameLength];

        return sanitized;
    }

    /// <summary>True when <paramref name="candidate"/> resolves to a path inside <paramref name="root"/>.</summary>
    private static bool IsUnder(string root, string candidate)
    {
        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }
}
