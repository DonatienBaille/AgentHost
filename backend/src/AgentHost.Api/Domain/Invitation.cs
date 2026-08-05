using System.Text.Json.Serialization;

namespace AgentHost.Api.Domain;

/// <summary>
/// A pending membership offer for one email address at one role inside one organization
/// (invitations table).
///
/// Like <see cref="RefreshToken"/>, only <see cref="TokenHash"/> — the SHA-256 hex of the raw
/// token — is stored; the raw token exists only in the response body of POST /api/invitations, is
/// never returned by any listing, and is never recoverable afterwards. Accepting is single-use:
/// <see cref="AcceptedAt"/> is set at that moment and <see cref="IsActive"/> is false forever after.
/// </summary>
public class Invitation
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;

    public string Email { get; set; } = string.Empty;

    /// <summary>Role the accepted user will be created with. Cannot be changed after issue.</summary>
    public UserRole Role { get; set; } = UserRole.Developer;

    /// <summary>Never serialized: exposing the hash would let a database reader forge acceptance.</summary>
    [JsonIgnore]
    public string TokenHash { get; set; } = string.Empty;

    public string InvitedByUserId { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Not yet accepted, not revoked, not expired — the only state in which it can be redeemed.</summary>
    public bool IsActive(DateTime now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;
}
