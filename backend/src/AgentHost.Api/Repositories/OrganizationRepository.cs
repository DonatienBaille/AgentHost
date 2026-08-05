using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IOrganizationRepository
{
    Task<Organization?> GetAsync(string id, CancellationToken ct = default);
    Task<Organization?> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<List<Organization>> ListAsync(CancellationToken ct = default);
    Task InsertAsync(Organization org, CancellationToken ct = default);
    Task UpdateAsync(Organization org, CancellationToken ct = default);
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

    public async Task<List<Organization>> ListAsync(CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM organizations WHERE deleted_at IS NULL ORDER BY created_at DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Organization>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.ToList();
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
}
