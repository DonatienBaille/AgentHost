using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IAgentRepository
{
    Task<Agent?> GetAsync(string id, CancellationToken ct = default);
    Task<Agent?> GetBySlugAsync(string orgId, string slug, CancellationToken ct = default);
    Task<List<Agent>> ListAsync(CancellationToken ct = default);
    Task<List<Agent>> ListByProjectAsync(string projectId, CancellationToken ct = default);
    Task InsertAsync(Agent agent, CancellationToken ct = default);
    Task UpdateAsync(Agent agent, CancellationToken ct = default);
}

public class AgentRepository : IAgentRepository
{
    private const string SelectColumns = """
        id, org_id, project_id, name, slug, agent_type, image_ref,
        manifest_yaml, inputs_schema, outputs_schema, current_version_id,
        is_published, created_at, updated_at, deleted_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public AgentRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Agent?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM agents WHERE id = @Id AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Agent>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<Agent?> GetBySlugAsync(string orgId, string slug, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM agents WHERE org_id = @OrgId AND slug = @Slug AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Agent>(new CommandDefinition(sql, new { OrgId = orgId, Slug = slug }, cancellationToken: ct));
    }

    public async Task<List<Agent>> ListAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM agents WHERE deleted_at IS NULL ORDER BY created_at DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Agent>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<Agent>> ListByProjectAsync(string projectId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM agents WHERE project_id = @ProjectId AND deleted_at IS NULL ORDER BY created_at DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Agent>(new CommandDefinition(sql, new { ProjectId = projectId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task InsertAsync(Agent agent, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO agents (
                id, org_id, project_id, name, slug, agent_type, image_ref,
                manifest_yaml, inputs_schema, outputs_schema, current_version_id,
                is_published, created_at, updated_at
            ) VALUES (
                @Id, @OrgId, @ProjectId, @Name, @Slug, @AgentType, @ImageRef,
                @ManifestYaml, @InputsSchema::jsonb, @OutputsSchema::jsonb, @CurrentVersionId,
                @IsPublished, @CreatedAt, @UpdatedAt
            )
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, agent, cancellationToken: ct));
        _logger.Information("Inserted agent {AgentId} ({Slug})", agent.Id, agent.Slug);
    }

    public async Task UpdateAsync(Agent agent, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE agents
            SET name = @Name,
                image_ref = @ImageRef,
                manifest_yaml = @ManifestYaml,
                inputs_schema = @InputsSchema::jsonb,
                outputs_schema = @OutputsSchema::jsonb,
                current_version_id = @CurrentVersionId,
                is_published = @IsPublished,
                updated_at = @UpdatedAt
            WHERE id = @Id
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, agent, cancellationToken: ct));
        _logger.Information("Updated agent {AgentId}", agent.Id);
    }
}
