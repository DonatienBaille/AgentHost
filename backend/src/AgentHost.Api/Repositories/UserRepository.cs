using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IUserRepository
{
    /// <summary>Unscoped lookup — only for resolving the *caller's own* identity (GET /api/auth/me).</summary>
    Task<User?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Org-scoped lookup: returns null (=&gt; 404, never 403) for another tenant's user.</summary>
    Task<User?> GetAsync(string id, string orgId, CancellationToken ct = default);

    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<List<User>> ListByOrgAsync(string orgId, CancellationToken ct = default);
    Task InsertAsync(User user, CancellationToken ct = default);
    Task UpdateAsync(User user, CancellationToken ct = default);
    Task SoftDeleteAsync(string id, string orgId, CancellationToken ct = default);
}

public class UserRepository : IUserRepository
{
    private const string SelectColumns = """
        id, org_id, email, display_name, avatar_url, role, password_hash,
        created_at, updated_at, deleted_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public UserRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<User?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM users WHERE id = @Id AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<UserRow>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<User?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM users WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<UserRow>(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<User?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM users WHERE email = @Email AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<UserRow>(new CommandDefinition(sql, new { Email = email }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<List<User>> ListByOrgAsync(string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM users WHERE org_id = @OrgId AND deleted_at IS NULL ORDER BY created_at DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<UserRow>(new CommandDefinition(sql, new { OrgId = orgId }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task InsertAsync(User user, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO users (id, org_id, email, display_name, avatar_url, role, password_hash, created_at, updated_at)
            VALUES (@Id, @OrgId, @Email, @DisplayName, @AvatarUrl, @Role, @PasswordHash, @CreatedAt, @UpdatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, UserRow.FromDomain(user), cancellationToken: ct));
        _logger.Information("Inserted user {UserId} ({Email})", user.Id, user.Email);
    }

    public async Task UpdateAsync(User user, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE users
            SET display_name = @DisplayName, avatar_url = @AvatarUrl, role = @Role,
                password_hash = @PasswordHash, updated_at = @UpdatedAt
            WHERE id = @Id
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, UserRow.FromDomain(user), cancellationToken: ct));
        _logger.Information("Updated user {UserId}", user.Id);
    }

    public async Task SoftDeleteAsync(string id, string orgId, CancellationToken ct = default)
    {
        const string sql = "UPDATE users SET deleted_at = NOW(), updated_at = NOW() WHERE id = @Id AND org_id = @OrgId";
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        _logger.Information("Soft-deleted user {UserId}", id);
    }
}
