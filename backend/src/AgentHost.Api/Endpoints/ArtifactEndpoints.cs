using System.Text;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Infrastructure.Storage;
using AgentHost.Api.Repositories;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Run artifact upload/download. Bytes go through <see cref="IArtifactStorage"/>, so the same routes
/// serve local disk (<c>Artifacts:Provider=local</c>, the default) and any S3-compatible object store
/// (<c>s3</c>: AWS S3, MinIO, Ceph). The <c>artifacts.s3_path</c> column stores whatever key that
/// provider derived — an absolute path for local disk, an object key for S3 — with no schema change.
///
/// Every route resolves the artifact (or its run) with the caller's org from the JWT, so one
/// tenant cannot read, list or write into another tenant's run directory. Downloads are streamed
/// through the API rather than redirected to a presigned URL; the reasoning is in
/// <see cref="S3ArtifactStorage"/>.
/// </summary>
public static class ArtifactEndpoints
{
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
        IArtifactStorage storage,
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

        var requestedName = string.IsNullOrWhiteSpace(name) ? file.FileName : name;
        var artifactName = SanitizeArtifactName(requestedName);
        if (artifactName is null)
            return Results.BadRequest(new { error = "Artifact name must contain at least one letter, digit, '.', '-' or '_'" });

        var artifactId = UlidGenerator.NewUlid();

        // runId comes from the route, so it is sanitized too — a run id is always a ULID, but the key
        // is built from it regardless of whether the lookup above happened to be strict. The provider
        // then re-checks that the key stays inside its own namespace and returns null if it does not.
        var safeRunId = SanitizeArtifactName(runId) ?? artifactId;
        var key = storage.DeriveKey(safeRunId, artifactId, artifactName);
        if (key is null) return Results.BadRequest(new { error = "Invalid artifact name or run id" });

        await using var upload = file.OpenReadStream();
        var sizeBytes = await storage.SaveAsync(key, upload, file.ContentType, ct);

        var artifact = new Artifact
        {
            Id = artifactId,
            RunId = runId,
            Name = artifactName,
            ArtifactType = artifactType,
            S3Path = key,
            SizeBytes = sizeBytes,
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

    private static async Task<IResult> DownloadArtifact(
        string id, IArtifactRepository repository, IArtifactStorage storage, ICallerContext caller, CancellationToken ct)
    {
        var artifact = await repository.GetAsync(id, caller.OrgId, ct);
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.S3Path)) return Results.NotFound();

        // Streamed, never buffered: Results.File copies the provider's stream to the response body
        // and disposes it afterwards, so a multi-gigabyte artifact costs one copy buffer.
        var stream = await storage.OpenReadAsync(artifact.S3Path, ct);
        if (stream is null) return Results.NotFound();

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
}
