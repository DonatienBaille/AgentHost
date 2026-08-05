using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

public static class ProjectEndpoints
{
    public static IEndpointRouteBuilder MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var projectsApi = app.MapGroup("/api/projects").WithTags("Projects").RequireAuthorization();

        projectsApi.MapGet("/", ListProjects).WithName("ListProjects");
        projectsApi.MapGet("/{id}", GetProject).WithName("GetProject");
        projectsApi.MapPost("/", CreateProject).WithName("CreateProject").WithValidation<CreateProjectRequest>()
            .RequireAuthorization(AuthorizationPolicies.Developer);
        projectsApi.MapPut("/{id}", UpdateProject).WithName("UpdateProject").WithValidation<UpdateProjectRequest>()
            .RequireAuthorization(AuthorizationPolicies.Developer);
        projectsApi.MapDelete("/{id}", DeleteProject).WithName("DeleteProject")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);

        return app;
    }

    /// <summary>Lists the caller's own organization's projects. There is no cross-org listing.</summary>
    private static async Task<IResult> ListProjects(IProjectService projectService, ICallerContext caller, CancellationToken ct)
    {
        var projects = await projectService.ListByOrgAsync(caller.OrgId, ct);
        return Results.Ok(projects);
    }

    private static async Task<IResult> GetProject(string id, IProjectService projectService, ICallerContext caller, CancellationToken ct)
    {
        var project = await projectService.GetAsync(id, caller.OrgId, ct);
        return project != null ? Results.Ok(project) : Results.NotFound();
    }

    private static async Task<IResult> CreateProject(
        CreateProjectRequest req, IProjectService projectService, ICallerContext caller, CancellationToken ct)
    {
        var project = await projectService.CreateAsync(req, caller.OrgId, ct);
        return Results.Created($"/api/projects/{project.Id}", project);
    }

    private static async Task<IResult> UpdateProject(
        string id, UpdateProjectRequest req, IProjectService projectService, ICallerContext caller, CancellationToken ct)
    {
        var project = await projectService.UpdateAsync(id, caller.OrgId, req, ct);
        return project != null ? Results.Ok(project) : Results.NotFound();
    }

    /// <summary>
    /// Soft-deletes the project and cascades to its agents, runs, project-scoped secrets and
    /// webhooks, so nothing beneath it is left live and readable.
    /// </summary>
    private static async Task<IResult> DeleteProject(
        string id, IProjectService projectService, IAuditService auditService, ICallerContext caller, CancellationToken ct)
    {
        var deleted = await projectService.DeleteAsync(id, caller.OrgId, ct);
        if (!deleted) return Results.NotFound();

        await auditService.RecordAsync(caller.OrgId, "project.deleted", caller.UserId, "project", id, ct: ct);
        return Results.NoContent();
    }
}
