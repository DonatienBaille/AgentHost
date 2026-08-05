using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Dapper;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IPasswordResetTokenRepository
{
    Task InsertAsync(PasswordResetToken token, CancellationToken ct = default);

    /// <summary>
    /// Looks a token up by the SHA-256 hex of its raw value. Unscoped by necessity: the confirm
    /// endpoint is anonymous and the token is the only credential — the user it applies to comes
    /// from the stored row.
    /// </summary>
    Task<PasswordResetToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>
    /// Consumes a token if (and only if) it is still active. Returns false when it was already
    /// used, revoked or expired — which is what makes a reset single-use under concurrency.
    /// </summary>
    Task<bool> MarkUsedAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Revokes every still-outstanding reset token for a user. Called when one is spent (so stale
    /// siblings die with it) and when the user changes their password by other means.
    /// </summary>
    Task<int> RevokeAllForUserAsync(string userId, CancellationToken ct = default);
}

public class PasswordResetTokenRepository : IPasswordResetTokenRepository
{
    private const string SelectColumns = """
        id, user_id, org_id, token_hash, expires_at, used_at, revoked_at, created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public PasswordResetTokenRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InsertAsync(PasswordResetToken token, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO password_reset_tokens (id, user_id, org_id, token_hash, expires_at, used_at, revoked_at, created_at)
            VALUES (@Id, @UserId, @OrgId, @TokenHash, @ExpiresAt, @UsedAt, @RevokedAt, @CreatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, token, cancellationToken: ct));
        // Deliberately no email in this log line: reset activity is enough, and the token hash
        // would make the log itself sensitive.
        _logger.Information("Issued password reset token for user {UserId}", token.UserId);
    }

    public async Task<PasswordResetToken?> GetByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM password_reset_tokens WHERE token_hash = @TokenHash";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<PasswordResetToken>(
            new CommandDefinition(sql, new { TokenHash = tokenHash }, cancellationToken: ct));
    }

    public async Task<bool> MarkUsedAsync(string id, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE password_reset_tokens SET used_at = NOW()
            WHERE id = @Id AND used_at IS NULL AND revoked_at IS NULL AND expires_at > NOW()
            """;
        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
        return affected == 1;
    }

    public async Task<int> RevokeAllForUserAsync(string userId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE password_reset_tokens SET revoked_at = NOW()
            WHERE user_id = @UserId AND used_at IS NULL AND revoked_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        return await db.ExecuteAsync(new CommandDefinition(sql, new { UserId = userId }, cancellationToken: ct));
    }
}
