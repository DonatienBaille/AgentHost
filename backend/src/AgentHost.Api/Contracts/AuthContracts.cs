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

public class AuthResponse
{
    public string Token { get; set; } = string.Empty;
    public User User { get; set; } = null!;
}
