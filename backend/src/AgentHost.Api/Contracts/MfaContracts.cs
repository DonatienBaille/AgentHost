namespace AgentHost.Api.Contracts;

/// <summary>
/// Result of POST /api/auth/mfa/enroll. The enrollment is <b>not</b> active yet: MFA only starts
/// gating logins once a correct code is presented to /api/auth/mfa/confirm.
/// </summary>
public class MfaEnrollResponse
{
    /// <summary>
    /// The TOTP shared secret, base32-encoded for manual entry. Shown here because the user has to
    /// transfer it to their authenticator; it is stored encrypted (never hashed — TOTP needs the
    /// seed back) and is not retrievable through any other endpoint.
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>Provisioning URI for QR rendering (<c>otpauth://totp/...</c>).</summary>
    public string OtpAuthUri { get; set; } = string.Empty;

    public int Digits { get; set; }
    public int PeriodSeconds { get; set; }
    public string Algorithm { get; set; } = "SHA1";
}

public class MfaConfirmRequest
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// Result of a successful confirmation. <see cref="RecoveryCodes"/> appears exactly once, here —
/// only hashes are kept, so they cannot be shown again; re-enrolling is the only way to get a new
/// set.
/// </summary>
public class MfaConfirmResponse
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Single-use codes for when the authenticator device is lost. Each one works once. Store them
    /// somewhere that is not the device generating the TOTP codes.
    /// </summary>
    public List<string> RecoveryCodes { get; set; } = new();
}

/// <summary>Turns MFA off. Requires proof of possession: a current TOTP code or an unused recovery code.</summary>
public class MfaDisableRequest
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>Second step of an MFA login: the challenge token from /api/auth/login plus a code.</summary>
public class MfaVerifyRequest
{
    /// <summary>
    /// The <c>mfaToken</c> returned by login. Not an access token and not accepted as one — see
    /// <see cref="Infrastructure.MfaChallengeTokenService"/>.
    /// </summary>
    public string MfaToken { get; set; } = string.Empty;

    /// <summary>A 6-digit TOTP code, or one of the recovery codes issued at confirmation time.</summary>
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// What POST /api/auth/login returns. It is a superset of <see cref="AuthResponse"/>: for an
/// account without MFA the token fields are populated exactly as before, and
/// <see cref="MfaRequired"/> is false.
///
/// When MFA <i>is</i> enabled, <see cref="Token"/> and <see cref="RefreshToken"/> are deliberately
/// empty — a correct password alone must not yield a usable session — and the caller gets
/// <see cref="MfaToken"/> to exchange at POST /api/auth/mfa/verify instead.
/// </summary>
public class LoginResponse
{
    /// <summary>True when the password was correct but a second factor is still required.</summary>
    public bool MfaRequired { get; set; }

    /// <summary>
    /// Short-lived, single-purpose challenge token; only present when <see cref="MfaRequired"/> is
    /// true. It authorizes exactly one call (/api/auth/mfa/verify) and is rejected by the normal
    /// bearer scheme.
    /// </summary>
    public string? MfaToken { get; set; }

    /// <summary>Lifetime of <see cref="MfaToken"/> in seconds; null when MFA is not required.</summary>
    public int? MfaExpiresInSeconds { get; set; }

    /// <summary>Access token. Empty when <see cref="MfaRequired"/> is true.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Refresh token. Empty when <see cref="MfaRequired"/> is true.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    public int ExpiresInSeconds { get; set; }

    /// <summary>The authenticated user — null while a login is still pending its second factor.</summary>
    public Domain.User? User { get; set; }

    public static LoginResponse Authenticated(AuthResponse auth) => new()
    {
        MfaRequired = false,
        Token = auth.Token,
        RefreshToken = auth.RefreshToken,
        ExpiresInSeconds = auth.ExpiresInSeconds,
        User = auth.User,
    };

    public static LoginResponse Challenge(string mfaToken, int expiresInSeconds) => new()
    {
        MfaRequired = true,
        MfaToken = mfaToken,
        MfaExpiresInSeconds = expiresInSeconds,
    };
}
