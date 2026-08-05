using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Project memory (spec 11.1). project_memories has no org_id of its own, so every route first
/// resolves the owning project with the caller's org from the JWT and 404s when it does not
/// belong to them — otherwise a guessed project ULID would expose (and let anyone overwrite)
/// another tenant's accumulated project context, decisions and learnings.
/// </summary>
public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{id}/memory", GetProjectMemory).WithTags("Memory").WithName("GetProjectMemory")
            .RequireAuthorization();
        app.MapPost("/api/projects/{id}/memory", UpdateProjectMemory).WithTags("Memory").WithName("UpdateProjectMemory")
            .RequireAuthorization(AuthorizationPolicies.Developer);
        app.MapPost("/api/projects/{id}/memory/archive", ArchiveOldRuns).WithTags("Memory").WithName("ArchiveProjectMemory")
            .RequireAuthorization(AuthorizationPolicies.Developer);

        return app;
    }

    private static async Task<IResult> GetProjectMemory(
        string id, IMemoryService memoryService, IProjectRepository projectRepository, ICallerContext caller, CancellationToken ct)
    {
        if (await projectRepository.GetAsync(id, caller.OrgId, ct) is null) return Results.NotFound();

        var memory = await memoryService.GetProjectMemoryAsync(id, ct);
        return Results.Ok(memory);
    }

    private static async Task<IResult> UpdateProjectMemory(
        string id,
        MemoryUpdate update,
        IMemoryService memoryService,
        IProjectRepository projectRepository,
        ICallerContext caller,
        CancellationToken ct)
    {
        if (await projectRepository.GetAsync(id, caller.OrgId, ct) is null) return Results.NotFound();

        await memoryService.UpdateProjectMemoryAsync(id, update, ct);
        var memory = await memoryService.GetProjectMemoryAsync(id, ct);
        return Results.Ok(memory);
    }

    private static async Task<IResult> ArchiveOldRuns(
        string id, IMemoryService memoryService, IProjectRepository projectRepository, ICallerContext caller, CancellationToken ct)
    {
        if (await projectRepository.GetAsync(id, caller.OrgId, ct) is null) return Results.NotFound();

        await memoryService.ArchiveOldRunsAsync(id, ct);
        return Results.Ok();
    }
}
