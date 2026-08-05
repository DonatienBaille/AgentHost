namespace AgentHost.Api.Domain;

public class User
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;
    public string? DisplayName { get; set; }
    public string? AvatarUrl { get; set; }

    public UserRole Role { get; set; } = UserRole.Developer;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
