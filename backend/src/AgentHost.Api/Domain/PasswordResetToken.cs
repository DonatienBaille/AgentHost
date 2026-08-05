using System.Text.Json.Serialization;

namespace AgentHost.Api.Domain;

/// <summary>
/// A short-lived, single-use credential that authorizes setting a new password for one user
/// (password_reset_tokens table).
///
/// This is the most dangerous token type in the system: presenting it is equivalent to knowing the
/// account's password. Hence the short lifetime, the hash-only storage (<see cref="TokenHash"/> is
/// all that is persisted), the single-use consumption, and the rule that confirming a reset revokes
/// every other outstanding token for the same user as well as all of their refresh tokens.
/// </summary>
public class PasswordResetToken
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;

    [JsonIgnore]
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsActive(DateTime now) => UsedAt is null && RevokedAt is null && ExpiresAt > now;
}
