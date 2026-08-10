using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IOrganizationRepository
{
    Task<Organization?> GetAsync(string id, CancellationToken ct = default);
    Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task InsertAsync(Organization org, CancellationToken ct = default);
    Task UpdateAsync(Organization org, CancellationToken ct = default);

    /// <summary>
    /// Soft-deletes the organization and everything beneath it (projects, agents, runs, secrets,
    /// users) in one transaction, and hard-deletes the webhooks of its projects (that table has
    /// no deleted_at column). Deleting only the org row would leave every child live and readable
    /// — an orphaned-tenant data leak and a GDPR erasure gap.
    /// </summary>
    Task SoftDeleteCascadeAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Efface définitivement une organisation <b>à condition qu'aucun utilisateur ne s'y rattache</b>.
    ///
    /// Il n'y a qu'un appelant, et il est décrit dans <c>AuthService.RegisterAsync</c> : une
    /// inscription insère l'organisation puis l'utilisateur, et rien ne peut réunir ces deux
    /// écritures dans une transaction sans faire passer une transaction à travers des dépôts qui
    /// n'en prennent pas. Quand la seconde échoue, la première doit être défaite — sinon chaque
    /// inscription en collision laisse une organisation vide et invisible.
    ///
    /// La condition n'est pas décorative : elle garantit que ce chemin ne peut pas effacer une
    /// organisation habitée, quelle que soit l'erreur qui l'a appelé.
    /// </summary>
    Task<bool> DeleteIfUninhabitedAsync(string id, CancellationToken ct = default);
}

public class OrganizationRepository : IOrganizationRepository
{
    private const string SelectColumns = "id, name, slug, plan, created_at, updated_at, deleted_at";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public OrganizationRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Organization?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM organizations WHERE id = @Id AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Organization>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM organizations WHERE slug = @Slug AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Organization>(new CommandDefinition(sql, new { Slug = slug }, cancellationToken: ct));
    }

    public async Task InsertAsync(Organization org, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO organizations (id, name, slug, plan, created_at, updated_at)
            VALUES (@Id, @Name, @Slug, @Plan, @CreatedAt, @UpdatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, org, cancellationToken: ct));
        _logger.Information("Inserted organization {OrgId} ({Slug})", org.Id, org.Slug);
    }

    public async Task UpdateAsync(Organization org, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE organizations
            SET name = @Name, plan = @Plan, updated_at = @UpdatedAt
            WHERE id = @Id
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, org, cancellationToken: ct));
        _logger.Information("Updated organization {OrgId}", org.Id);
    }

    public async Task<bool> DeleteIfUninhabitedAsync(string id, CancellationToken ct = default)
    {
        const string sql = """
            DELETE FROM organizations
            WHERE id = @Id AND NOT EXISTS (SELECT 1 FROM users WHERE org_id = @Id)
            """;
        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
        return affected > 0;
    }

    public async Task SoftDeleteCascadeAsync(string id, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        using var tx = db.BeginTransaction();
        try
        {
            var args = new { Id = id };

            // webhooks has no deleted_at column, so the cascade is a hard delete there. It must
            // run before projects are marked deleted so the subquery can still see them.
            await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM webhooks WHERE project_id IN (SELECT id FROM projects WHERE org_id = @Id)",
                args, tx, cancellationToken: ct));

            foreach (var table in new[] { "runs", "agents", "secrets", "projects", "users" })
            {
                await db.ExecuteAsync(new CommandDefinition(
                    $"UPDATE {table} SET deleted_at = NOW(), updated_at = NOW() WHERE org_id = @Id AND deleted_at IS NULL",
                    args, tx, cancellationToken: ct));
            }

            await db.ExecuteAsync(new CommandDefinition(
                "UPDATE organizations SET deleted_at = NOW(), updated_at = NOW() WHERE id = @Id AND deleted_at IS NULL",
                args, tx, cancellationToken: ct));

            tx.Commit();
            _logger.Information("Soft-deleted organization {OrgId} and all of its projects/agents/runs/secrets/users", id);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
