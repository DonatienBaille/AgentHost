using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Dapper;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IUserMfaRepository
{
    /// <summary>
    /// The user's TOTP enrollment, confirmed or not. Unscoped by design: every caller resolves
    /// either their own id (from their token) or an id already proven by an MFA challenge token.
    /// </summary>
    Task<UserMfa?> GetAsync(string userId, CancellationToken ct = default);

    /// <summary>Creates or replaces the enrollment, resetting it to the unconfirmed state.</summary>
    Task UpsertAsync(UserMfa mfa, CancellationToken ct = default);

    /// <summary>Flips an enrollment to enabled after a correct code. False when there was nothing pending.</summary>
    Task<bool> EnableAsync(string userId, CancellationToken ct = default);

    /// <summary>Removes the enrollment and every recovery code with it.</summary>
    Task DeleteAsync(string userId, CancellationToken ct = default);

    /// <summary>Replaces the user's recovery codes wholesale — issuing a new set invalidates the old.</summary>
    Task ReplaceRecoveryCodesAsync(string userId, IEnumerable<MfaRecoveryCode> codes, CancellationToken ct = default);

    /// <summary>
    /// Consumes a recovery code by hash if it exists and has not been used. Returns false
    /// otherwise, which is what makes replaying a spent code fail.
    /// </summary>
    Task<bool> ConsumeRecoveryCodeAsync(string userId, string codeHash, CancellationToken ct = default);

    /// <summary>How many of the user's recovery codes are still unused.</summary>
    Task<int> CountUnusedRecoveryCodesAsync(string userId, CancellationToken ct = default);
}

public class UserMfaRepository : IUserMfaRepository
{
    private const string SelectColumns = """
        user_id, org_id, secret_encrypted, enabled, confirmed_at, created_at, updated_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public UserMfaRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<UserMfa?> GetAsync(string userId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM user_mfa WHERE user_id = @UserId";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<UserMfa>(
            new CommandDefinition(sql, new { UserId = userId }, cancellationToken: ct));
    }

    public async Task UpsertAsync(UserMfa mfa, CancellationToken ct = default)
    {
        // Re-enrolling resets confirmation: a fresh secret must be proven before it can gate logins.
        const string sql = """
            INSERT INTO user_mfa (user_id, org_id, secret_encrypted, enabled, confirmed_at, created_at, updated_at)
            VALUES (@UserId, @OrgId, @SecretEncrypted, @Enabled, @ConfirmedAt, @CreatedAt, @UpdatedAt)
            ON CONFLICT (user_id) DO UPDATE SET
                secret_encrypted = EXCLUDED.secret_encrypted,
                enabled = EXCLUDED.enabled,
                confirmed_at = EXCLUDED.confirmed_at,
                updated_at = EXCLUDED.updated_at
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, mfa, cancellationToken: ct));
    }

    public async Task<bool> EnableAsync(string userId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE user_mfa SET enabled = TRUE, confirmed_at = NOW(), updated_at = NOW()
            WHERE user_id = @UserId
            """;
        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { UserId = userId }, cancellationToken: ct));
        if (affected == 1) _logger.Information("MFA enabled for user {UserId}", userId);
        return affected == 1;
    }

    public async Task DeleteAsync(string userId, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mfa_recovery_codes WHERE user_id = @UserId", new { UserId = userId }, cancellationToken: ct));
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM user_mfa WHERE user_id = @UserId", new { UserId = userId }, cancellationToken: ct));
        _logger.Information("MFA disabled for user {UserId}", userId);
    }

    public async Task ReplaceRecoveryCodesAsync(
        string userId, IEnumerable<MfaRecoveryCode> codes, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mfa_recovery_codes WHERE user_id = @UserId", new { UserId = userId }, cancellationToken: ct));

        const string insert = """
            INSERT INTO mfa_recovery_codes (id, user_id, code_hash, used_at, created_at)
            VALUES (@Id, @UserId, @CodeHash, @UsedAt, @CreatedAt)
            """;
        await db.ExecuteAsync(new CommandDefinition(insert, codes.ToList(), cancellationToken: ct));
    }

    public async Task<bool> ConsumeRecoveryCodeAsync(string userId, string codeHash, CancellationToken ct = default)
    {
        // Conditional UPDATE, not read-then-write: two concurrent uses of the same code cannot both
        // match, so a recovery code is genuinely single-use.
        const string sql = """
            UPDATE mfa_recovery_codes SET used_at = NOW()
            WHERE user_id = @UserId AND code_hash = @CodeHash AND used_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(
            new CommandDefinition(sql, new { UserId = userId, CodeHash = codeHash }, cancellationToken: ct));
        if (affected == 1) _logger.Information("MFA recovery code consumed for user {UserId}", userId);
        return affected == 1;
    }

    public async Task<int> CountUnusedRecoveryCodesAsync(string userId, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM mfa_recovery_codes WHERE user_id = @UserId AND used_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { UserId = userId }, cancellationToken: ct));
    }
}
