using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

public interface IMfaService
{
    /// <summary>Starts (or restarts) enrollment: a fresh secret, stored encrypted and not yet active.</summary>
    Task<MfaEnrollResponse> EnrollAsync(User user, CancellationToken ct = default);

    /// <summary>
    /// Confirms an enrollment with a live TOTP code, enabling MFA and returning the recovery codes
    /// once. Null when there is no pending enrollment or the code is wrong.
    /// </summary>
    Task<MfaConfirmResponse?> ConfirmAsync(User user, string code, CancellationToken ct = default);

    /// <summary>
    /// Turns MFA off, but only for a caller who can present a current TOTP code or an unused
    /// recovery code. False otherwise.
    /// </summary>
    Task<bool> DisableAsync(User user, string code, CancellationToken ct = default);

    /// <summary>
    /// Second step of an MFA login: validates the challenge token and the code (TOTP or recovery),
    /// and only then issues the real access+refresh pair. Null for any failure.
    /// </summary>
    Task<AuthResponse?> VerifyChallengeAsync(string mfaToken, string code, CancellationToken ct = default);
}

/// <summary>
/// TOTP multi-factor authentication (RFC 6238; the algorithm itself is <see cref="Totp"/>).
///
/// The property that makes this worth having is the login step-up: once MFA is enabled,
/// <c>POST /api/auth/login</c> stops returning a usable session for a correct password and returns
/// a challenge token instead, which only <see cref="VerifyChallengeAsync"/> accepts and which the
/// bearer scheme refuses (see <see cref="MfaChallengeTokenService"/>). Without that, MFA would be a
/// checkbox that changes nothing about what a stolen password gets you.
///
/// Storage: the TOTP seed is encrypted with <see cref="ISecretsBroker"/> (AES-256-GCM) rather than
/// hashed, because verification needs the seed back. Recovery codes, which do not, are stored as
/// SHA-256 hashes like every other opaque token here and are single-use.
/// </summary>
public class MfaService : IMfaService
{
    /// <summary>How many recovery codes an enrollment issues.</summary>
    public const int RecoveryCodeCount = 10;

    /// <summary>Bytes of entropy per recovery code (~80 bits once base32-encoded).</summary>
    private const int RecoveryCodeEntropyBytes = 10;

    private readonly IUserMfaRepository _mfaRepository;
    private readonly IUserRepository _userRepository;
    private readonly ISecretsBroker _secretsBroker;
    private readonly IAuthTokenIssuer _tokenIssuer;
    private readonly IMfaChallengeTokenService _challengeTokens;
    private readonly IAuditService _auditService;
    private readonly ILogger _logger;
    private readonly string _issuerName;

    public MfaService(
        IUserMfaRepository mfaRepository,
        IUserRepository userRepository,
        ISecretsBroker secretsBroker,
        IAuthTokenIssuer tokenIssuer,
        IMfaChallengeTokenService challengeTokens,
        IAuditService auditService,
        IConfiguration config,
        ILogger logger)
    {
        _mfaRepository = mfaRepository;
        _userRepository = userRepository;
        _secretsBroker = secretsBroker;
        _tokenIssuer = tokenIssuer;
        _challengeTokens = challengeTokens;
        _auditService = auditService;
        _logger = logger;
        _issuerName = config["Jwt:Issuer"] ?? "agenthost";
    }

    public async Task<MfaEnrollResponse> EnrollAsync(User user, CancellationToken ct = default)
    {
        var base32Secret = Base32.Encode(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(Totp.SecretBytes));
        var now = DateTime.UtcNow;

        // enabled = false: a mis-scanned QR code must not be able to lock a user out of their own
        // account. Only ConfirmAsync, with a working code, flips this on.
        await _mfaRepository.UpsertAsync(new UserMfa
        {
            UserId = user.Id,
            OrgId = user.OrgId,
            // Encrypted, not hashed: TOTP verification needs the seed itself back.
            SecretEncrypted = _secretsBroker.Encrypt(base32Secret),
            Enabled = false,
            ConfirmedAt = null,
            CreatedAt = now,
            UpdatedAt = now,
        }, ct);

        return new MfaEnrollResponse
        {
            Secret = base32Secret,
            OtpAuthUri = Totp.BuildOtpAuthUri(_issuerName, user.Email, base32Secret),
            Digits = Totp.DefaultDigits,
            PeriodSeconds = Totp.DefaultStepSeconds,
        };
    }

    public async Task<MfaConfirmResponse?> ConfirmAsync(User user, string code, CancellationToken ct = default)
    {
        var mfa = await _mfaRepository.GetAsync(user.Id, ct);
        if (mfa is null) return null;

        if (!Totp.VerifyCode(DecryptSecret(mfa), code, DateTimeOffset.UtcNow))
            return null;

        await _mfaRepository.EnableAsync(user.Id, ct);

        // A new set of recovery codes replaces any previous set, so codes from an abandoned
        // enrollment never stay valid.
        var (raw, records) = GenerateRecoveryCodes(user.Id);
        await _mfaRepository.ReplaceRecoveryCodesAsync(user.Id, records, ct);

        await _auditService.RecordAsync(user.OrgId, "mfa.enabled", user.Id, "user", user.Id, ct: ct);

        return new MfaConfirmResponse { Enabled = true, RecoveryCodes = raw };
    }

    public async Task<bool> DisableAsync(User user, string code, CancellationToken ct = default)
    {
        var mfa = await _mfaRepository.GetAsync(user.Id, ct);
        if (mfa is null) return false;

        // Proof of possession, not just of the session: an attacker holding a stolen access token
        // must not be able to strip the second factor off the account.
        if (!await VerifyCodeOrRecoveryAsync(mfa, user.Id, code, ct))
            return false;

        await _mfaRepository.DeleteAsync(user.Id, ct);
        await _auditService.RecordAsync(user.OrgId, "mfa.disabled", user.Id, "user", user.Id, ct: ct);
        return true;
    }

    public async Task<AuthResponse?> VerifyChallengeAsync(
        string mfaToken, string code, CancellationToken ct = default)
    {
        var userId = _challengeTokens.ValidateAndGetUserId(mfaToken);
        if (userId is null) return null;

        // The user id comes from the signed challenge token, never from the request body.
        var user = await _userRepository.GetAsync(userId, ct);
        if (user is null) return null;

        var mfa = await _mfaRepository.GetAsync(userId, ct);
        if (mfa is null || !mfa.Enabled) return null;

        if (!await VerifyCodeOrRecoveryAsync(mfa, userId, code, ct))
        {
            _logger.Warning("MFA verification failed for user {UserId}", userId);
            return null;
        }

        var (response, _) = await _tokenIssuer.IssueAsync(user, ct);
        return response;
    }

    /// <summary>
    /// Accepts either a live TOTP code or an unused recovery code. The recovery branch consumes the
    /// code, so replaying it fails — that is enforced by a conditional UPDATE in the repository.
    /// </summary>
    private async Task<bool> VerifyCodeOrRecoveryAsync(
        UserMfa mfa, string userId, string code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;

        if (Totp.VerifyCode(DecryptSecret(mfa), code, DateTimeOffset.UtcNow))
            return true;

        return await _mfaRepository.ConsumeRecoveryCodeAsync(userId, HashRecoveryCode(code), ct);
    }

    private byte[] DecryptSecret(UserMfa mfa) => Base32.Decode(_secretsBroker.Decrypt(mfa.SecretEncrypted));

    private static (List<string> Raw, List<MfaRecoveryCode> Records) GenerateRecoveryCodes(string userId)
    {
        var now = DateTime.UtcNow;
        var raw = new List<string>(RecoveryCodeCount);
        var records = new List<MfaRecoveryCode>(RecoveryCodeCount);

        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            // Base32 of 10 random bytes, split into two groups for legibility when written down.
            var value = Base32.Encode(System.Security.Cryptography.RandomNumberGenerator.GetBytes(RecoveryCodeEntropyBytes));
            var formatted = $"{value[..8]}-{value[8..]}";

            raw.Add(formatted);
            records.Add(new MfaRecoveryCode
            {
                Id = UlidGenerator.NewUlid(),
                UserId = userId,
                CodeHash = HashRecoveryCode(formatted),
                CreatedAt = now,
            });
        }

        return (raw, records);
    }

    /// <summary>
    /// Normalizes before hashing (upper case, dashes and spaces stripped) so a user retyping a code
    /// from paper is not defeated by formatting.
    /// </summary>
    private static string HashRecoveryCode(string code) =>
        OpaqueToken.Hash(code.Trim().ToUpperInvariant().Replace("-", string.Empty).Replace(" ", string.Empty));
}
