using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Dapper;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IInvitationRepository
{
    Task InsertAsync(Invitation invitation, CancellationToken ct = default);

    /// <summary>
    /// Looks an invitation up by the SHA-256 hex of its raw token. Deliberately *not* org-scoped:
    /// POST /api/invitations/accept is anonymous and the token itself is the only credential — the
    /// org the user lands in comes from the stored row, never from the request.
    /// </summary>
    Task<Invitation?> GetByHashAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>Org-scoped lookup: another tenant's invitation reads as absent (=&gt; 404, never 403).</summary>
    Task<Invitation?> GetAsync(string id, string orgId, CancellationToken ct = default);

    Task<List<Invitation>> ListByOrgAsync(string orgId, CancellationToken ct = default);

    /// <summary>The still-redeemable invitation for an email (case-insensitive), if any — used to reject duplicates.</summary>
    Task<Invitation?> GetActiveByEmailAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Marks an invitation accepted, but only if it is still active. Returns false when another
    /// request got there first, which is what makes acceptance single-use under concurrency.
    /// </summary>
    Task<bool> MarkAcceptedAsync(string id, CancellationToken ct = default);

    /// <summary>Revokes a pending invitation. Returns false when it was already spent or revoked.</summary>
    Task<bool> RevokeAsync(string id, string orgId, CancellationToken ct = default);
}

public class InvitationRepository : IInvitationRepository
{
    private const string SelectColumns = """
        id, org_id, email, role, token_hash, invited_by_user_id,
        expires_at, accepted_at, revoked_at, created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public InvitationRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InsertAsync(Invitation invitation, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO invitations (id, org_id, email, role, token_hash, invited_by_user_id,
                                     expires_at, accepted_at, revoked_at, created_at)
            VALUES (@Id, @OrgId, @Email, @Role, @TokenHash, @InvitedByUserId,
                    @ExpiresAt, @AcceptedAt, @RevokedAt, @CreatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, InvitationRow.FromDomain(invitation), cancellationToken: ct));
        _logger.Information("Created invitation {InvitationId} for {Email} at role {Role} in org {OrgId}",
            invitation.Id, invitation.Email, invitation.Role.ToDbString(), invitation.OrgId);
    }

    public async Task<Invitation?> GetByHashAsync(string tokenHash, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM invitations WHERE token_hash = @TokenHash";
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<InvitationRow>(
            new CommandDefinition(sql, new { TokenHash = tokenHash }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<Invitation?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM invitations WHERE id = @Id AND org_id = @OrgId";
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<InvitationRow>(
            new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<List<Invitation>> ListByOrgAsync(string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM invitations WHERE org_id = @OrgId ORDER BY created_at DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<InvitationRow>(new CommandDefinition(sql, new { OrgId = orgId }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<Invitation?> GetActiveByEmailAsync(string email, CancellationToken ct = default)
    {
        // Unscoped across orgs on purpose: an email may only have one outstanding invitation
        // anywhere, because acceptance creates a user and users.email is globally unique.
        var sql = $"""
            SELECT {SelectColumns} FROM invitations
            WHERE LOWER(email) = LOWER(@Email)
              AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > NOW()
            ORDER BY created_at DESC
            LIMIT 1
            """;
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<InvitationRow>(
            new CommandDefinition(sql, new { Email = email }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<bool> MarkAcceptedAsync(string id, CancellationToken ct = default)
    {
        // The WHERE clause is the single-use guarantee: two concurrent accepts of the same token
        // both read an active row, but only one UPDATE matches.
        const string sql = """
            UPDATE invitations SET accepted_at = NOW()
            WHERE id = @Id AND accepted_at IS NULL AND revoked_at IS NULL AND expires_at > NOW()
            """;
        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
        return affected == 1;
    }

    public async Task<bool> RevokeAsync(string id, string orgId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE invitations SET revoked_at = NOW()
            WHERE id = @Id AND org_id = @OrgId AND accepted_at IS NULL AND revoked_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        if (affected == 1) _logger.Information("Revoked invitation {InvitationId}", id);
        return affected == 1;
    }
}
