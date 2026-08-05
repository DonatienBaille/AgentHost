using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

public interface IAgentService
{
    /// <summary>
    /// Unscoped lookup — for the run executor, which resolves the agent of a run whose caller was
    /// already authorized, from a background scope with no caller org. Request handlers must use
    /// the org-scoped overload.
    /// </summary>
    Task<Agent?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Org-scoped lookup: returns null (=&gt; 404, never 403) for another tenant's agent.</summary>
    Task<Agent?> GetAsync(string id, string orgId, CancellationToken ct = default);

    Task<List<Agent>> ListByOrgAsync(string orgId, CancellationToken ct = default);
    Task<List<Agent>> ListByProjectAsync(string projectId, string orgId, CancellationToken ct = default);
    Task<Agent> CreateAsync(CreateAgentRequest req, string orgId, CancellationToken ct = default);
    Task<Agent?> UpdateAsync(string id, string orgId, UpdateAgentRequest req, CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, string orgId, CancellationToken ct = default);
}

/// <summary>
/// Agent registry: parses/validates manifests (spec 6.2), stores the agent + an immutable
/// version snapshot, and keeps `agents.current_version_id` pointed at the latest snapshot.
/// </summary>
public class AgentService : IAgentService
{
    private readonly IAgentRepository _agentRepository;
    private readonly IAgentVersionRepository _agentVersionRepository;
    private readonly IAgentManifestParser _manifestParser;
    private readonly ILogger _logger;

    public AgentService(
        IAgentRepository agentRepository,
        IAgentVersionRepository agentVersionRepository,
        IAgentManifestParser manifestParser,
        ILogger logger)
    {
        _agentRepository = agentRepository;
        _agentVersionRepository = agentVersionRepository;
        _manifestParser = manifestParser;
        _logger = logger;
    }

    public Task<Agent?> GetAsync(string id, CancellationToken ct = default) => _agentRepository.GetAsync(id, ct);

    public Task<Agent?> GetAsync(string id, string orgId, CancellationToken ct = default) =>
        _agentRepository.GetAsync(id, orgId, ct);

    public Task<List<Agent>> ListByOrgAsync(string orgId, CancellationToken ct = default) =>
        _agentRepository.ListByOrgAsync(orgId, ct);

    public Task<List<Agent>> ListByProjectAsync(string projectId, string orgId, CancellationToken ct = default) =>
        _agentRepository.ListByProjectAsync(projectId, orgId, ct);

    public async Task<Agent> CreateAsync(CreateAgentRequest req, string orgId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(orgId)) throw new ArgumentException("OrgId required");
        if (string.IsNullOrWhiteSpace(req.ProjectId)) throw new ArgumentException("ProjectId required");
        if (string.IsNullOrWhiteSpace(req.ManifestYaml)) throw new ArgumentException("ManifestYaml required");

        var manifest = _manifestParser.Parse(req.ManifestYaml);

        var name = string.IsNullOrWhiteSpace(req.Name) ? manifest.Metadata.DisplayName : req.Name;
        var slug = string.IsNullOrWhiteSpace(req.Slug) ? Slugify(manifest.Metadata.Name) : req.Slug;

        var agentType = ParseAgentType(manifest.Spec.Type);
        var inputsSchema = JsonSerializer.Serialize(manifest.Spec.Inputs);
        var outputsSchema = manifest.Spec.Outputs is null ? null : JsonSerializer.Serialize(manifest.Spec.Outputs);

        var now = DateTime.UtcNow;
        var agent = new Agent
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = orgId,
            ProjectId = req.ProjectId,
            Name = name,
            Slug = slug,
            AgentType = agentType,
            ImageRef = manifest.Spec.Image,
            ManifestYaml = req.ManifestYaml,
            InputsSchema = inputsSchema,
            OutputsSchema = outputsSchema,
            CurrentVersionId = null,
            IsPublished = req.Publish,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _agentRepository.InsertAsync(agent, ct);

        var version = new AgentVersion
        {
            Id = UlidGenerator.NewUlid(),
            AgentId = agent.Id,
            VersionNumber = 1,
            ManifestYaml = req.ManifestYaml,
            ImageRef = manifest.Spec.Image,
            InputsSchema = inputsSchema,
            OutputsSchema = outputsSchema,
            DigestSha256 = ComputeDigest(req.ManifestYaml),
            CreatedAt = now,
        };

        await _agentVersionRepository.InsertAsync(version, ct);

        agent.CurrentVersionId = version.Id;
        await _agentRepository.UpdateAsync(agent, ct);

        _logger.Information("Created agent {AgentId} ({Slug}) with initial version {VersionId}", agent.Id, agent.Slug, version.Id);

        return agent;
    }

    public async Task<Agent?> UpdateAsync(string id, string orgId, UpdateAgentRequest req, CancellationToken ct = default)
    {
        var agent = await _agentRepository.GetAsync(id, orgId, ct);
        if (agent is null) return null;

        if (req.Name is not null) agent.Name = req.Name;
        agent.UpdatedAt = DateTime.UtcNow;

        await _agentRepository.UpdateAsync(agent, ct);
        return agent;
    }

    public async Task<bool> DeleteAsync(string id, string orgId, CancellationToken ct = default)
    {
        var agent = await _agentRepository.GetAsync(id, orgId, ct);
        if (agent is null) return false;

        await _agentRepository.SoftDeleteAsync(id, orgId, ct);
        return true;
    }

    private static AgentType ParseAgentType(string type) => type switch
    {
        "oci" => AgentType.Oci,
        "copilot" => AgentType.Copilot,
        "claude_code" => AgentType.ClaudeCode,
        "openai" => AgentType.OpenAi,
        "custom" => AgentType.Custom,
        _ => AgentType.Custom,
    };

    private static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return UlidGenerator.NewUlid().ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in value.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }

    private static string ComputeDigest(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
