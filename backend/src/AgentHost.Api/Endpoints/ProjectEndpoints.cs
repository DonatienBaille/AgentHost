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

        return app;
    }

    private static async Task<IResult> ListProjects(IProjectService projectService, CancellationToken ct, string? orgId = null)
    {
        var projects = orgId is null
            ? await projectService.ListAsync(ct)
            : await projectService.ListByOrgAsync(orgId, ct);
        return Results.Ok(projects);
    }

    private static async Task<IResult> GetProject(string id, IProjectService projectService, CancellationToken ct)
    {
        var project = await projectService.GetAsync(id, ct);
        return project != null ? Results.Ok(project) : Results.NotFound();
    }

    private static async Task<IResult> CreateProject(CreateProjectRequest req, IProjectService projectService, CancellationToken ct)
    {
        var project = await projectService.CreateAsync(req, ct);
        return Results.Created($"/api/projects/{project.Id}", project);
    }

    private static async Task<IResult> UpdateProject(string id, UpdateProjectRequest req, IProjectService projectService, CancellationToken ct)
    {
        var project = await projectService.UpdateAsync(id, req, ct);
        return project != null ? Results.Ok(project) : Results.NotFound();
    }
}
