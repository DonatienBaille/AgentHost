using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IRefreshTokenRepository
{
    Task InsertAsync(RefreshToken token, CancellationToken ct = default);

    /// <summary>Looks a token up by the SHA-256 hex of its raw value. Raw values are never stored.</summary>
    Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Marks a single token revoked, optionally recording the token that replaced it.</summary>
    Task RevokeAsync(string id, string? replacedById, CancellationToken ct = default);

    /// <summary>Revokes every still-active token for a user — the server side of "log out".</summary>
    Task<int> RevokeAllForUserAsync(string userId, CancellationToken ct = default);
}

public class RefreshTokenRepository : IRefreshTokenRepository
{
    private const string SelectColumns = """
        id, user_id, org_id, token_hash, expires_at, revoked_at, replaced_by_id, created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public RefreshTokenRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InsertAsync(RefreshToken token, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO refresh_tokens (id, user_id, org_id, token_hash, expires_at, revoked_at, replaced_by_id, created_at)
            VALUES (@Id, @UserId, @OrgId, @TokenHash, @ExpiresAt, @RevokedAt, @ReplacedById, @CreatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, token, cancellationToken: ct));
    }

    public async Task<RefreshToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM refresh_tokens WHERE token_hash = @TokenHash";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<RefreshToken>(
            new CommandDefinition(sql, new { TokenHash = tokenHash }, cancellationToken: ct));
    }

    public async Task RevokeAsync(string id, string? replacedById, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE refresh_tokens
            SET revoked_at = NOW(), replaced_by_id = @ReplacedById
            WHERE id = @Id AND revoked_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id, ReplacedById = replacedById }, cancellationToken: ct));
    }

    public async Task<int> RevokeAllForUserAsync(string userId, CancellationToken ct = default)
    {
        const string sql = "UPDATE refresh_tokens SET revoked_at = NOW() WHERE user_id = @UserId AND revoked_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { UserId = userId }, cancellationToken: ct));
        _logger.Information("Revoked {Count} refresh token(s) for user {UserId}", affected, userId);
        return affected;
    }
}
