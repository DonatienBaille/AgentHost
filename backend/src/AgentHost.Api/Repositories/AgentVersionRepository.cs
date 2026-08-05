using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>
/// agent_versions has no org_id column, so tenant scoping joins through the owning agent
/// (agent_versions.agent_id -&gt; agents.org_id).
/// </summary>
public interface IAgentVersionRepository
{
    Task<AgentVersion?> GetAsync(string id, string orgId, CancellationToken ct = default);
    Task<List<AgentVersion>> ListByAgentAsync(string agentId, string orgId, CancellationToken ct = default);
    Task<int> GetNextVersionNumberAsync(string agentId, CancellationToken ct = default);
    Task InsertAsync(AgentVersion version, CancellationToken ct = default);
}

public class AgentVersionRepository : IAgentVersionRepository
{
    private const string SelectColumns = """
        id, agent_id, version_number, manifest_yaml, image_ref,
        inputs_schema, outputs_schema, digest_sha256, created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public AgentVersionRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private const string ScopedSelectColumns = """
        v.id, v.agent_id, v.version_number, v.manifest_yaml, v.image_ref,
        v.inputs_schema, v.outputs_schema, v.digest_sha256, v.created_at
        """;

    public async Task<AgentVersion?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {ScopedSelectColumns} FROM agent_versions v
            JOIN agents a ON a.id = v.agent_id
            WHERE v.id = @Id AND a.org_id = @OrgId AND a.deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<AgentVersion>(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
    }

    public async Task<List<AgentVersion>> ListByAgentAsync(string agentId, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {ScopedSelectColumns} FROM agent_versions v
            JOIN agents a ON a.id = v.agent_id
            WHERE v.agent_id = @AgentId AND a.org_id = @OrgId AND a.deleted_at IS NULL
            ORDER BY v.version_number DESC
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<AgentVersion>(new CommandDefinition(sql, new { AgentId = agentId, OrgId = orgId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<int> GetNextVersionNumberAsync(string agentId, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        var next = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COALESCE(MAX(version_number), 0) + 1 FROM agent_versions WHERE agent_id = @AgentId",
            new { AgentId = agentId },
            cancellationToken: ct));
        return next;
    }

    public async Task InsertAsync(AgentVersion version, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO agent_versions (
                id, agent_id, version_number, manifest_yaml, image_ref,
                inputs_schema, outputs_schema, digest_sha256, created_at
            ) VALUES (
                @Id, @AgentId, @VersionNumber, @ManifestYaml, @ImageRef,
                @InputsSchema::jsonb, @OutputsSchema::jsonb, @DigestSha256, @CreatedAt
            )
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, version, cancellationToken: ct));
        _logger.Information("Inserted agent version {VersionId} (v{VersionNumber}) for agent {AgentId}",
            version.Id, version.VersionNumber, version.AgentId);
    }
}
