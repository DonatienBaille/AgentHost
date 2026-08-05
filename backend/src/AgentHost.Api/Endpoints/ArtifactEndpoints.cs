using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Run artifact upload/download. Files are written to local disk under
/// <c>Artifacts:StoragePath</c> (default "/var/agenthost/artifacts"); the <c>artifacts.s3_path</c>
/// column currently stores that local absolute path, not a real s3:// URI — an S3/blob storage
/// backend is future work, this is a local-disk stand-in for it.
/// </summary>
public static class ArtifactEndpoints
{
    private const string DefaultStoragePath = "/var/agenthost/artifacts";

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
        CancellationToken ct)
    {
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data body required" });

        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"];
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "'file' is required" });

        var name = form["name"].FirstOrDefault();
        var artifactType = form["artifactType"].FirstOrDefault();

        var run = await runRepository.GetAsync(runId, ct);
        if (run is null) return Results.NotFound();

        var storagePath = configuration["Artifacts:StoragePath"];
        if (string.IsNullOrWhiteSpace(storagePath)) storagePath = DefaultStoragePath;

        var artifactId = UlidGenerator.NewUlid();
        var artifactName = string.IsNullOrWhiteSpace(name) ? file.FileName : name;

        var runDir = Path.Combine(storagePath, runId);
        Directory.CreateDirectory(runDir);

        var filePath = Path.Combine(runDir, $"{artifactId}-{artifactName}");
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

    private static async Task<IResult> ListArtifactsForRun(string runId, IArtifactRepository repository, CancellationToken ct)
    {
        var artifacts = await repository.ListByRunAsync(runId, ct);
        return Results.Ok(artifacts);
    }

    private static async Task<IResult> GetArtifact(string id, IArtifactRepository repository, CancellationToken ct)
    {
        var artifact = await repository.GetAsync(id, ct);
        return artifact != null ? Results.Ok(artifact) : Results.NotFound();
    }

    private static async Task<IResult> DownloadArtifact(string id, IArtifactRepository repository, CancellationToken ct)
    {
        var artifact = await repository.GetAsync(id, ct);
        if (artifact is null || !File.Exists(artifact.S3Path)) return Results.NotFound();

        var stream = File.OpenRead(artifact.S3Path);
        return Results.File(stream, "application/octet-stream", fileDownloadName: artifact.Name);
    }
}
