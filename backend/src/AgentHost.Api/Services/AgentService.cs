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
    Task<Agent?> GetAsync(string id, CancellationToken ct = default);
    Task<List<Agent>> ListAsync(CancellationToken ct = default);
    Task<List<Agent>> ListByProjectAsync(string projectId, CancellationToken ct = default);
    Task<Agent> CreateAsync(CreateAgentRequest req, CancellationToken ct = default);
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

    public Task<List<Agent>> ListAsync(CancellationToken ct = default) => _agentRepository.ListAsync(ct);

    public Task<List<Agent>> ListByProjectAsync(string projectId, CancellationToken ct = default) =>
        _agentRepository.ListByProjectAsync(projectId, ct);

    public async Task<Agent> CreateAsync(CreateAgentRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.OrgId)) throw new ArgumentException("OrgId required");
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
            OrgId = req.OrgId,
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
