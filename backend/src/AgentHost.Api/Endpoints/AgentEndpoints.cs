using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
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

        return app;
    }

    private static async Task<IResult> ListAgents(IAgentService agentService, CancellationToken ct, string? projectId = null)
    {
        var agents = projectId is null
            ? await agentService.ListAsync(ct)
            : await agentService.ListByProjectAsync(projectId, ct);
        return Results.Ok(agents);
    }

    private static async Task<IResult> GetAgent(string id, IAgentService agentService, CancellationToken ct)
    {
        var agent = await agentService.GetAsync(id, ct);
        return agent != null ? Results.Ok(agent) : Results.NotFound();
    }

    private static async Task<IResult> CreateAgent(CreateAgentRequest req, IAgentService agentService, CancellationToken ct)
    {
        try
        {
            var agent = await agentService.CreateAsync(req, ct);
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
}
