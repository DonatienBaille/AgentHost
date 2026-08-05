using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

/// <summary>Self-service signup: creates a new organization and its first (owner) user.</summary>
public class RegisterRequest
{
    public string OrgName { get; set; } = string.Empty;
    public string OrgSlug { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
}

public class LoginRequest
{
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class RefreshRequest
{
    public string RefreshToken { get; set; } = string.Empty;
}

public class AuthResponse
{
    /// <summary>
    /// Short-lived access token (Jwt:ExpiryMinutes, 15 min by default). Access tokens are
    /// stateless and therefore remain valid until they expire even after logout — that is by
    /// design; the short TTL is the mitigation, and <see cref="RefreshToken"/> is the revocable
    /// half of the pair.
    /// </summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Opaque, revocable refresh token. Only its hash is persisted server-side.</summary>
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Access token lifetime in seconds, so clients know when to refresh.</summary>
    public int ExpiresInSeconds { get; set; }

    public User User { get; set; } = null!;
}

/// <summary>
/// Starts a password reset for an email address. The response is always 202 with the same body
/// shape whether or not the address exists — see the endpoint's doc comment for why.
/// </summary>
public class PasswordResetRequestRequest
{
    public string Email { get; set; } = string.Empty;
}

/// <summary>
/// Response to POST /api/auth/password-reset/request. Identical for existing and non-existing
/// addresses, so it cannot be used to enumerate accounts.
/// </summary>
public class PasswordResetRequestResponse
{
    /// <summary>Constant, deliberately uninformative acknowledgement.</summary>
    public string Message { get; set; } =
        "If that email address has an account, a password reset token has been issued.";

    /// <summary>
    /// The raw reset token — ONLY ever populated when <c>Auth:ReturnResetTokenInResponse</c> is
    /// explicitly enabled, which is a development/test affordance for a deployment with no mailer.
    /// It is null in every default and production configuration; see the endpoint's doc comment.
    /// </summary>
    public string? Token { get; set; }
}

/// <summary>Completes a password reset using the token from the request step.</summary>
public class PasswordResetConfirmRequest
{
    public string Token { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}

/// <summary>Authenticated self-service password change; proves possession of the current password.</summary>
public class ChangePasswordRequest
{
    public string CurrentPassword { get; set; } = string.Empty;
    public string NewPassword { get; set; } = string.Empty;
}
