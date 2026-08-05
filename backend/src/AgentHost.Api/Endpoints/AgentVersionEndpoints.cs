using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Agent version publishing/history (spec: agent_versions table). Publishing a new version
/// creates an immutable snapshot and repoints the parent agent's current_version_id at it.
/// </summary>
public static class AgentVersionEndpoints
{
    public static IEndpointRouteBuilder MapAgentVersionEndpoints(this IEndpointRouteBuilder app)
    {
        var versionsApi = app.MapGroup("/api/agents/{agentId}/versions").WithTags("AgentVersions").RequireAuthorization();

        versionsApi.MapGet("/", ListAgentVersions).WithName("ListAgentVersions");
        versionsApi.MapGet("/{versionId}", GetAgentVersion).WithName("GetAgentVersion");
        versionsApi.MapPost("/", PublishAgentVersion).WithName("PublishAgentVersion").WithValidation<PublishAgentVersionRequest>()
            .RequireAuthorization(AuthorizationPolicies.Developer);

        return app;
    }

    private static async Task<IResult> ListAgentVersions(
        string agentId, IAgentVersionRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var versions = await repository.ListByAgentAsync(agentId, caller.OrgId, ct);
        return Results.Ok(versions);
    }

    private static async Task<IResult> GetAgentVersion(
        string agentId, string versionId, IAgentVersionRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var version = await repository.GetAsync(versionId, caller.OrgId, ct);
        return version != null && version.AgentId == agentId ? Results.Ok(version) : Results.NotFound();
    }

    private static async Task<IResult> PublishAgentVersion(
        string agentId,
        PublishAgentVersionRequest req,
        IAgentRepository agentRepository,
        IAgentVersionRepository agentVersionRepository,
        ICallerContext caller,
        CancellationToken ct)
    {
        var agent = await agentRepository.GetAsync(agentId, caller.OrgId, ct);
        if (agent is null) return Results.NotFound();

        var inputsSchema = req.InputsSchema is null ? agent.InputsSchema : JsonSerializer.Serialize(req.InputsSchema);
        var outputsSchema = req.OutputsSchema is null ? agent.OutputsSchema : JsonSerializer.Serialize(req.OutputsSchema);

        var versionNumber = await agentVersionRepository.GetNextVersionNumberAsync(agentId, ct);
        var now = DateTime.UtcNow;

        var version = new AgentVersion
        {
            Id = UlidGenerator.NewUlid(),
            AgentId = agentId,
            VersionNumber = versionNumber,
            ManifestYaml = req.ManifestYaml,
            ImageRef = req.ImageRef ?? agent.ImageRef,
            InputsSchema = inputsSchema,
            OutputsSchema = outputsSchema,
            DigestSha256 = ComputeDigest(req.ManifestYaml),
            CreatedAt = now,
        };

        await agentVersionRepository.InsertAsync(version, ct);

        agent.ManifestYaml = req.ManifestYaml;
        agent.InputsSchema = inputsSchema;
        agent.OutputsSchema = outputsSchema;
        agent.ImageRef = version.ImageRef;
        agent.CurrentVersionId = version.Id;
        agent.IsPublished = true;
        agent.UpdatedAt = now;

        await agentRepository.UpdateAsync(agent, ct);

        return Results.Created($"/api/agents/{agentId}/versions/{version.Id}", version);
    }

    private static string ComputeDigest(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
