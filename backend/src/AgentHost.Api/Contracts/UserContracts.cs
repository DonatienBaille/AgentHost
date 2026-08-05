using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

public class CreateUserRequest
{
    // No OrgId: a maintainer may only create users inside their own organization, which is read
    // from their JWT. Accepting it from the body was a direct privilege-escalation path into any
    // other tenant (create an owner-role user in a victim org, then log in as them).
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public UserRole Role { get; set; } = UserRole.Developer;
}

public class UpdateUserRequest
{
    public string? DisplayName { get; set; }
    public UserRole? Role { get; set; }
    public string? Password { get; set; }
}
