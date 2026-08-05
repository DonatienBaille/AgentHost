using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>Repository for the `project_memories` table (spec 11.1 dream-like project memory).</summary>
public interface IMemoryRepository
{
    Task<ProjectMemory?> GetAsync(string projectId, CancellationToken ct = default);
    Task InsertAsync(ProjectMemory memory, CancellationToken ct = default);
    Task UpdateAsync(string projectId, ProjectMemory memory, CancellationToken ct = default);
}

public class MemoryRepository : IMemoryRepository
{
    private const string SelectColumns = """
        project_id, context, run_history, patterns, learnings, notes, archived,
        created_at, updated_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public MemoryRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<ProjectMemory?> GetAsync(string projectId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM project_memories WHERE project_id = @ProjectId";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<ProjectMemory>(new CommandDefinition(sql, new { ProjectId = projectId }, cancellationToken: ct));
    }

    public async Task InsertAsync(ProjectMemory memory, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO project_memories (
                project_id, context, run_history, patterns, learnings, notes, archived,
                created_at, updated_at
            ) VALUES (
                @ProjectId, @Context::jsonb, @RunHistory::jsonb, @Patterns::jsonb,
                @Learnings::jsonb, @Notes::jsonb, @Archived::jsonb,
                @CreatedAt, @UpdatedAt
            )
            ON CONFLICT (project_id) DO NOTHING
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, memory, cancellationToken: ct));
        _logger.Information("Inserted project memory for project {ProjectId}", memory.ProjectId);
    }

    public async Task UpdateAsync(string projectId, ProjectMemory memory, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE project_memories
            SET context = @Context::jsonb,
                run_history = @RunHistory::jsonb,
                patterns = @Patterns::jsonb,
                learnings = @Learnings::jsonb,
                notes = @Notes::jsonb,
                archived = @Archived::jsonb,
                updated_at = @UpdatedAt
            WHERE project_id = @ProjectId
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, new
        {
            ProjectId = projectId,
            memory.Context,
            memory.RunHistory,
            memory.Patterns,
            memory.Learnings,
            memory.Notes,
            memory.Archived,
            memory.UpdatedAt,
        }, cancellationToken: ct));
        _logger.Information("Updated project memory for project {ProjectId}", projectId);
    }
}
