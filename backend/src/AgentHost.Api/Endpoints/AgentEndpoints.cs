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
        // Declared before "/{id}" for readability only: the two never collide (GET vs POST), and
        // this route is authenticated but not role-gated — it reads nothing and writes nothing,
        // it only echoes back what the caller just typed.
        agentsApi.MapPost("/validate-manifest", ValidateManifest).WithName("ValidateAgentManifest");
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

    /// <summary>
    /// Dry-runs the manifest parser so the editor can validate as the user types, without creating
    /// anything. Answers 200 for both outcomes — "this does not parse yet" is the expected reply
    /// mid-keystroke, not a client error — and carries the failure in the body instead.
    /// </summary>
    private static IResult ValidateManifest(ValidateManifestRequest req, IAgentManifestParser parser)
    {
        try
        {
            var manifest = parser.Parse(req.ManifestYaml);
            var policy = parser.ParseContainerPolicy(req.ManifestYaml);
            return Results.Ok(new ValidateManifestResponse
            {
                Valid = true,
                Manifest = manifest,
                PermissionExtensions = new ManifestPermissionExtensions
                {
                    NetworkAllowlist = policy.NetworkAllowlist.ToList(),
                    WritableRootfs = policy.WritableRootfs,
                },
                Document = YamlDocumentProjection.ToJson(req.ManifestYaml),
            });
        }
        catch (Exception ex)
        {
            return Results.Ok(new ValidateManifestResponse { Valid = false, Error = Locate(ex) });
        }
    }

    /// <summary>
    /// Turns a parser exception into a located error. The parser wraps YamlDotNet failures in an
    /// <see cref="InvalidOperationException"/>, so the position lives on an inner
    /// <see cref="YamlDotNet.Core.YamlException"/> — when there is one at all. Its own
    /// structural errors (empty document, missing <c>spec</c>, missing <c>metadata.name</c>) carry
    /// no position, and are reported without one rather than with a made-up line.
    /// </summary>
    private static ManifestValidationError Locate(Exception exception)
    {
        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            if (ex is not YamlDotNet.Core.YamlException yaml) continue;
            return new ManifestValidationError
            {
                Message = exception.Message,
                Line = yaml.Start.Line > 0 ? (int)yaml.Start.Line : null,
                Column = yaml.Start.Column > 0 ? (int)yaml.Start.Column : null,
            };
        }

        return new ManifestValidationError { Message = exception.Message };
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
