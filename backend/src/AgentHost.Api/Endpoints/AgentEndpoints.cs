using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

public static class AgentEndpoints
{
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        var agentsApi = app.MapGroup("/api/agents").WithTags("Agents").RequireAuthorization();

        agentsApi.MapGet("/", ListAgents).WithName("ListAgents");
        agentsApi.MapGet("/{id}", GetAgent).WithName("GetAgent");
        agentsApi.MapPost("/", CreateAgent).WithName("CreateAgent").WithValidation<CreateAgentRequest>()
            .RequireAuthorization(AuthorizationPolicies.Developer);
        agentsApi.MapPut("/{id}", UpdateAgent).WithName("UpdateAgent")
            .RequireAuthorization(AuthorizationPolicies.Developer);
        agentsApi.MapDelete("/{id}", DeleteAgent).WithName("DeleteAgent")
            .RequireAuthorization(AuthorizationPolicies.Developer);

        return app;
    }

    /// <summary>Lists the caller's own organization's agents, optionally narrowed to one project.</summary>
    private static async Task<IResult> ListAgents(
        IAgentService agentService, ICallerContext caller, CancellationToken ct, string? projectId = null)
    {
        var agents = projectId is null
            ? await agentService.ListByOrgAsync(caller.OrgId, ct)
            : await agentService.ListByProjectAsync(projectId, caller.OrgId, ct);
        return Results.Ok(agents);
    }

    private static async Task<IResult> GetAgent(string id, IAgentService agentService, ICallerContext caller, CancellationToken ct)
    {
        var agent = await agentService.GetAsync(id, caller.OrgId, ct);
        return agent != null ? Results.Ok(agent) : Results.NotFound();
    }

    private static async Task<IResult> CreateAgent(
        CreateAgentRequest req,
        IAgentService agentService,
        IProjectRepository projectRepository,
        ICallerContext caller,
        CancellationToken ct)
    {
        // The agent's org comes from the token; the project it is filed under must belong to that
        // same org, or an attacker could attach agents to another tenant's project.
        var project = await projectRepository.GetAsync(req.ProjectId, caller.OrgId, ct);
        if (project is null) return Results.NotFound(new { error = "Project not found" });

        try
        {
            var agent = await agentService.CreateAsync(req, caller.OrgId, ct);
            return Results.Created($"/api/agents/{agent.Id}", agent);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }
    }

    private static async Task<IResult> UpdateAgent(
        string id, UpdateAgentRequest req, IAgentService agentService, ICallerContext caller, CancellationToken ct)
    {
        var agent = await agentService.UpdateAsync(id, caller.OrgId, req, ct);
        return agent != null ? Results.Ok(agent) : Results.NotFound();
    }

    private static async Task<IResult> DeleteAgent(string id, IAgentService agentService, ICallerContext caller, CancellationToken ct)
    {
        var deleted = await agentService.DeleteAsync(id, caller.OrgId, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
