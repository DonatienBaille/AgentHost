using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IProjectRepository
{
    Task<Project?> GetAsync(string id, CancellationToken ct = default);
    Task<Project?> GetBySlugAsync(string orgId, string slug, CancellationToken ct = default);
    Task<List<Project>> ListAsync(CancellationToken ct = default);
    Task<List<Project>> ListByOrgAsync(string orgId, CancellationToken ct = default);
    Task InsertAsync(Project project, CancellationToken ct = default);
    Task UpdateAsync(Project project, CancellationToken ct = default);
}

public class ProjectRepository : IProjectRepository
{
    private const string SelectColumns = """
        id, org_id, name, slug, description, budget_monthly_usd,
        created_at, updated_at, deleted_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public ProjectRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Project?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM projects WHERE id = @Id AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Project>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<Project?> GetBySlugAsync(string orgId, string slug, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM projects WHERE org_id = @OrgId AND slug = @Slug AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Project>(new CommandDefinition(sql, new { OrgId = orgId, Slug = slug }, cancellationToken: ct));
    }

    public async Task<List<Project>> ListAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM projects WHERE deleted_at IS NULL ORDER BY created_at DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Project>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<Project>> ListByOrgAsync(string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM projects WHERE org_id = @OrgId AND deleted_at IS NULL ORDER BY created_at DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Project>(new CommandDefinition(sql, new { OrgId = orgId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task InsertAsync(Project project, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO projects (id, org_id, name, slug, description, budget_monthly_usd, created_at, updated_at)
            VALUES (@Id, @OrgId, @Name, @Slug, @Description, @BudgetMonthlyUsd, @CreatedAt, @UpdatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, project, cancellationToken: ct));
        _logger.Information("Inserted project {ProjectId} ({Slug})", project.Id, project.Slug);
    }

    public async Task UpdateAsync(Project project, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE projects
            SET name = @Name, description = @Description, budget_monthly_usd = @BudgetMonthlyUsd, updated_at = @UpdatedAt
            WHERE id = @Id
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, project, cancellationToken: ct));
        _logger.Information("Updated project {ProjectId}", project.Id);
    }
}
