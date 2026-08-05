using AgentHost.Api.Contracts;
using AgentHost.Api.Services;

namespace AgentHost.Api.Endpoints;

public static class MemoryEndpoints
{
    public static IEndpointRouteBuilder MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/projects/{id}/memory", GetProjectMemory).WithTags("Memory").WithName("GetProjectMemory");
        app.MapPost("/api/projects/{id}/memory", UpdateProjectMemory).WithTags("Memory").WithName("UpdateProjectMemory");
        app.MapPost("/api/projects/{id}/memory/archive", ArchiveOldRuns).WithTags("Memory").WithName("ArchiveProjectMemory");

        return app;
    }

    private static async Task<IResult> GetProjectMemory(string id, IMemoryService memoryService, CancellationToken ct)
    {
        var memory = await memoryService.GetProjectMemoryAsync(id, ct);
        return Results.Ok(memory);
    }

    private static async Task<IResult> UpdateProjectMemory(string id, MemoryUpdate update, IMemoryService memoryService, CancellationToken ct)
    {
        await memoryService.UpdateProjectMemoryAsync(id, update, ct);
        var memory = await memoryService.GetProjectMemoryAsync(id, ct);
        return Results.Ok(memory);
    }

    private static async Task<IResult> ArchiveOldRuns(string id, IMemoryService memoryService, CancellationToken ct)
    {
        await memoryService.ArchiveOldRunsAsync(id, ct);
        return Results.Ok();
    }
}
