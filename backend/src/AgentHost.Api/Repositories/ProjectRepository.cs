using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IProjectRepository
{
    /// <summary>
    /// Unscoped lookup — for background/system callers only (e.g. the run executor, which has no
    /// HTTP caller). Request handlers must use the org-scoped overload.
    /// </summary>
    Task<Project?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Org-scoped lookup: returns null (=&gt; 404, never 403) for another tenant's project.</summary>
    Task<Project?> GetAsync(string id, string orgId, CancellationToken ct = default);

    Task<Project?> GetBySlugAsync(string orgId, string slug, CancellationToken ct = default);
    Task<List<Project>> ListByOrgAsync(string orgId, CancellationToken ct = default);
    Task InsertAsync(Project project, CancellationToken ct = default);
    Task UpdateAsync(Project project, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the project and everything beneath it (agents, runs) in one transaction, and
    /// hard-deletes its webhooks (the webhooks table has no deleted_at column). Without the
    /// cascade the children stay live and readable, which is both a GDPR erasure gap and a
    /// dangling-reference bug.
    /// </summary>
    Task SoftDeleteCascadeAsync(string id, string orgId, CancellationToken ct = default);
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

    public async Task<Project?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM projects WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Project>(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
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

    public async Task SoftDeleteCascadeAsync(string id, string orgId, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        using var tx = db.BeginTransaction();
        try
        {
            var args = new { Id = id, OrgId = orgId };

            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE runs SET deleted_at = NOW(), updated_at = NOW() WHERE project_id = @Id AND org_id = @OrgId AND deleted_at IS NULL",
                args, tx, cancellationToken: ct));
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE agents SET deleted_at = NOW(), updated_at = NOW() WHERE project_id = @Id AND org_id = @OrgId AND deleted_at IS NULL",
                args, tx, cancellationToken: ct));
            // Project-scoped secrets die with the project; org-scoped ones survive it.
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE secrets SET deleted_at = NOW(), updated_at = NOW() WHERE project_id = @Id AND org_id = @OrgId AND deleted_at IS NULL",
                args, tx, cancellationToken: ct));
            // webhooks has no deleted_at column, so the cascade is a hard delete there.
            await db.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM webhooks WHERE project_id IN (
                    SELECT id FROM projects WHERE id = @Id AND org_id = @OrgId
                )
                """,
                args, tx, cancellationToken: ct));
            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE projects SET deleted_at = NOW(), updated_at = NOW() WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL",
                args, tx, cancellationToken: ct));

            tx.Commit();
            _logger.Information("Soft-deleted project {ProjectId} and its agents/runs/secrets/webhooks", id);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
