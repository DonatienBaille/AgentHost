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
