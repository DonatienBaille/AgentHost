namespace AgentHost.Api.Domain;

/// <summary>
/// A persisted, revocable refresh token (refresh_tokens table). Only <see cref="TokenHash"/> —
/// the SHA-256 hex of the raw token — is ever stored or compared; the raw value exists solely in
/// the response body handed to the client at login/refresh time.
/// </summary>
public class RefreshToken
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;

    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? ReplacedById { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool IsActive(DateTime now) => RevokedAt is null && ExpiresAt > now;
}
