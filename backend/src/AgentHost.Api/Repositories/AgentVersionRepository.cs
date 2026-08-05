using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IAgentVersionRepository
{
    Task<AgentVersion?> GetAsync(string id, CancellationToken ct = default);
    Task<List<AgentVersion>> ListByAgentAsync(string agentId, CancellationToken ct = default);
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

    public async Task<AgentVersion?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM agent_versions WHERE id = @Id";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<AgentVersion>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<List<AgentVersion>> ListByAgentAsync(string agentId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM agent_versions WHERE agent_id = @AgentId ORDER BY version_number DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<AgentVersion>(new CommandDefinition(sql, new { AgentId = agentId }, cancellationToken: ct));
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
