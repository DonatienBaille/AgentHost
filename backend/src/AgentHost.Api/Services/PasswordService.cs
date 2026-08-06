using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services.Email;
using Serilog;

namespace AgentHost.Api.Services;

public interface IPasswordService
{
    /// <summary>
    /// True when <c>Auth:ReturnResetTokenInResponse</c> is on, i.e. this instance hands the raw
    /// reset token back to the anonymous caller. Development/test only — see
    /// <see cref="RequestResetAsync"/>.
    /// </summary>
    bool ReturnsResetTokenInResponse { get; }

    /// <summary>
    /// Mints a reset token when the email belongs to a user, and does nothing when it does not.
    /// When a mailer is configured the reset link is queued for delivery to that address. Returns
    /// the raw token only when <see cref="ReturnsResetTokenInResponse"/> is on; callers must answer
    /// 202 identically either way.
    /// </summary>
    Task<string?> RequestResetAsync(string email, CancellationToken ct = default);

    /// <summary>
    /// Consumes a reset token, sets the new password, and ends every existing session for that
    /// user. False for any invalid token — unknown, expired, revoked or already used.
    /// </summary>
    Task<bool> ConfirmResetAsync(PasswordResetConfirmRequest req, CancellationToken ct = default);

    /// <summary>
    /// Changes the caller's own password after verifying the current one, then revokes their
    /// refresh tokens and issues a fresh pair so the calling session survives and every other one
    /// does not. Null when the current password is wrong.
    /// </summary>
    Task<AuthResponse?> ChangePasswordAsync(string userId, ChangePasswordRequest req, CancellationToken ct = default);
}

/// <summary>
/// Password reset (anonymous, token-based) and self-service password change (authenticated).
///
/// Three properties this implementation is built around:
///
///  1. <b>No account enumeration.</b> The request endpoint's answer does not depend on whether the
///     email exists — same status, same body, same work done as far as an outside observer can
///     tell. An endpoint that 404s on unknown addresses is a free user-directory oracle.
///  2. <b>Reset tokens are single-use and short-lived</b>, stored only as a SHA-256 hash, and
///     spending one revokes every other outstanding token for that user.
///  3. <b>A password change ends sessions.</b> Both the reset and the change path revoke the
///     user's refresh tokens: the whole point of resetting a password is usually that someone else
///     may have had it, and leaving their 14-day refresh token alive would make the reset
///     cosmetic. Access tokens already issued still live out their (15 minute) TTL — the system
///     keeps no JWT deny-list, which is documented in <see cref="AuthService"/>.
///
/// <b>Delivery.</b> The reset link is emailed to the address that requested it, through
/// <see cref="IEmailDispatcher"/> — which queues the message and returns immediately, so neither
/// the mail relay's latency nor its failures can be observed on the response. A deployment that
/// configures no mailer (<c>Email:Provider</c> = none, the default) still mints and stores the
/// token but nothing is delivered, which is the historical behaviour of this repository; the
/// dev-only <c>Auth:ReturnResetTokenInResponse</c> flag remains the escape hatch for that case.
/// See <see cref="RequestResetAsync"/>.
/// </summary>
public class PasswordService : IPasswordService
{
    /// <summary>
    /// Reset tokens live 30 minutes. Much shorter than an invitation because possession of one is
    /// equivalent to knowing the password.
    /// </summary>
    public static readonly TimeSpan ResetTokenLifetime = TimeSpan.FromMinutes(30);

    private readonly IUserRepository _userRepository;
    private readonly IPasswordResetTokenRepository _resetTokenRepository;
    private readonly IRefreshTokenRepository _refreshTokenRepository;
    private readonly IAuthTokenIssuer _tokenIssuer;
    private readonly IAuditService _auditService;
    private readonly IEmailDispatcher _emailDispatcher;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger _logger;
    private readonly bool _returnResetTokenInResponse;

    public PasswordService(
        IUserRepository userRepository,
        IPasswordResetTokenRepository resetTokenRepository,
        IRefreshTokenRepository refreshTokenRepository,
        IAuthTokenIssuer tokenIssuer,
        IAuditService auditService,
        IEmailDispatcher emailDispatcher,
        EmailOptions emailOptions,
        IConfiguration config,
        ILogger logger)
    {
        _userRepository = userRepository;
        _resetTokenRepository = resetTokenRepository;
        _refreshTokenRepository = refreshTokenRepository;
        _tokenIssuer = tokenIssuer;
        _auditService = auditService;
        _emailDispatcher = emailDispatcher;
        _emailOptions = emailOptions;
        _logger = logger;

        // Absent or unparseable => OFF. Handing a reset token to an anonymous caller is a full
        // account-takeover primitive, so it must never be something a deployment gets by accident.
        _returnResetTokenInResponse =
            bool.TryParse(config["Auth:ReturnResetTokenInResponse"], out var value) && value;

        if (_returnResetTokenInResponse)
        {
            _logger.Warning(
                "Auth:ReturnResetTokenInResponse is ENABLED: POST /api/auth/password-reset/request returns the " +
                "raw reset token to any anonymous caller who names an existing email address. This is an " +
                "account-takeover primitive and is only acceptable in development/test.");
        }
    }

    public bool ReturnsResetTokenInResponse => _returnResetTokenInResponse;

    /// <summary>
    /// When the email belongs to a user a token is minted, stored (hashed) and its link queued for
    /// delivery to that address; when it does not, nothing happens at all. Either way the caller
    /// must answer identically.
    ///
    /// <b>Why the send is queued rather than awaited.</b> This is the endpoint's
    /// no-account-enumeration property, and it is fragile: awaiting an SMTP round trip would make
    /// the response measurably slower — and its failure modes visible — exactly and only when the
    /// account exists, which is the same oracle the flat 202 exists to close. Enqueuing is a
    /// non-blocking in-memory write whose cost is orders of magnitude below the Postgres round
    /// trips this branch already performs, so it adds no new observable difference between the two
    /// branches. See <see cref="BackgroundEmailDispatcher"/>.
    ///
    /// <b>What comes back.</b> With <c>Auth:ReturnResetTokenInResponse</c> off (the default, and
    /// the only correct production setting) the raw token is never returned; the emailed link is
    /// then the only way to obtain it, which is what makes the flow usable in production. With the
    /// flag on, the raw token is also returned to the caller so development and tests can exercise
    /// the flow end to end without a mailer — in production that would let anyone reset any account
    /// whose email address they can name.
    /// </summary>
    public async Task<string?> RequestResetAsync(string email, CancellationToken ct = default)
    {
        var user = await _userRepository.GetByEmailAsync(email.Trim(), ct);
        if (user is null)
        {
            // No such user: do nothing, and let the endpoint answer exactly as it would have.
            _logger.Information("Password reset requested for an unknown email address; answering 202 regardless");
            return null;
        }

        // One outstanding token at a time: a new request supersedes any earlier one, so a token
        // from a request the user has forgotten about cannot be spent later.
        await _resetTokenRepository.RevokeAllForUserAsync(user.Id, ct);

        var rawToken = OpaqueToken.New();
        var now = DateTime.UtcNow;

        await _resetTokenRepository.InsertAsync(new PasswordResetToken
        {
            Id = UlidGenerator.NewUlid(),
            UserId = user.Id,
            OrgId = user.OrgId,
            TokenHash = OpaqueToken.Hash(rawToken),
            ExpiresAt = now.Add(ResetTokenLifetime),
            CreatedAt = now,
        }, ct);

        await _auditService.RecordAsync(user.OrgId, "password_reset.requested", user.Id, "user", user.Id, ct: ct);

        // Fire-and-forget by construction: Enqueue never blocks and never throws, so nothing about
        // the mail relay can reach this request — see the summary above. With no mailer configured
        // this lands in NoOpEmailSender, which logs that the message did not go out.
        _emailDispatcher.Enqueue(EmailTemplates.PasswordReset(
            user.Email,
            _emailOptions.PasswordResetLink(rawToken),
            ResetTokenLifetime,
            _emailOptions.Language));

        return _returnResetTokenInResponse ? rawToken : null;
    }

    public async Task<bool> ConfirmResetAsync(PasswordResetConfirmRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Token)) return false;

        var stored = await _resetTokenRepository.GetByHashAsync(OpaqueToken.Hash(req.Token), ct);
        if (stored is null || !stored.IsActive(DateTime.UtcNow))
            return false;

        var user = await _userRepository.GetAsync(stored.UserId, ct);
        if (user is null) return false;

        // Consume first: if this loses a race with a concurrent confirm, stop rather than applying
        // the second password on top of the first.
        if (!await _resetTokenRepository.MarkUsedAsync(stored.Id, ct))
            return false;

        user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user, ct);

        // Any sibling token minted by an earlier request dies with this one.
        await _resetTokenRepository.RevokeAllForUserAsync(user.Id, ct);

        // A reset exists because the old password may be compromised; leaving the old sessions
        // alive would defeat it. This is what makes the reset actually end existing sessions.
        var revoked = await _refreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);
        _logger.Information("Password reset completed for user {UserId}; revoked {Count} refresh token(s)",
            user.Id, revoked);

        await _auditService.RecordAsync(user.OrgId, "password_reset.completed", user.Id, "user", user.Id, ct: ct);
        return true;
    }

    public async Task<AuthResponse?> ChangePasswordAsync(
        string userId, ChangePasswordRequest req, CancellationToken ct = default)
    {
        // Unscoped by design: this resolves the caller's *own* id, taken from their token.
        var user = await _userRepository.GetAsync(userId, ct);
        if (user is null || !PasswordHasher.Verify(req.CurrentPassword, user.PasswordHash))
            return null;

        user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
        user.UpdatedAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(user, ct);

        // Outstanding reset tokens are stale the moment the password changes by another route.
        await _resetTokenRepository.RevokeAllForUserAsync(user.Id, ct);

        // Revoke everything, then hand the caller a brand-new pair: the session that performed the
        // change keeps working, every other session is logged out.
        var revoked = await _refreshTokenRepository.RevokeAllForUserAsync(user.Id, ct);
        _logger.Information("Password changed for user {UserId}; revoked {Count} refresh token(s)", user.Id, revoked);

        await _auditService.RecordAsync(user.OrgId, "password.changed", user.Id, "user", user.Id, ct: ct);

        var (response, _) = await _tokenIssuer.IssueAsync(user, ct);
        return response;
    }
}
