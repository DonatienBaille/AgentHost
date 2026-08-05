using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

/// <summary>
/// Invites an email address into the *caller's own* organization. There is deliberately no OrgId:
/// it comes from the inviter's token, so an invitation can never plant a user in another tenant.
/// </summary>
public class CreateInvitationRequest
{
    public string Email { get; set; } = string.Empty;

    /// <summary>Role the invitee is created with on acceptance. Fixed at invite time.</summary>
    public UserRole Role { get; set; } = UserRole.Developer;
}

/// <summary>
/// Metadata about an invitation. This is what GET /api/invitations returns — note the absence of
/// any token field: the raw token is only ever present on <see cref="CreateInvitationResponse"/>,
/// and its hash is never exposed at all.
/// </summary>
public class InvitationResponse
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public string InvitedByUserId { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
    public DateTime? AcceptedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Redeemable right now — i.e. neither accepted, nor revoked, nor expired.</summary>
    public bool Active { get; set; }

    public static InvitationResponse From(Invitation invitation, DateTime now) => new()
    {
        Id = invitation.Id,
        OrgId = invitation.OrgId,
        Email = invitation.Email,
        Role = invitation.Role,
        InvitedByUserId = invitation.InvitedByUserId,
        ExpiresAt = invitation.ExpiresAt,
        AcceptedAt = invitation.AcceptedAt,
        RevokedAt = invitation.RevokedAt,
        CreatedAt = invitation.CreatedAt,
        Active = invitation.IsActive(now),
    };
}

/// <summary>
/// The response to POST /api/invitations, and the only place <see cref="Token"/> ever appears.
/// The server keeps a SHA-256 hash, so this value is unrecoverable once the response is discarded —
/// a lost token means revoking the invitation and issuing a new one.
/// </summary>
public class CreateInvitationResponse
{
    public InvitationResponse Invitation { get; set; } = null!;

    /// <summary>
    /// The raw invitation token, shown exactly once. There is no mailer in this system: the
    /// inviter must deliver this to the invitee out of band.
    /// </summary>
    public string Token { get; set; } = string.Empty;
}

/// <summary>
/// Redeems an invitation token. Anonymous by necessity — the invitee has no account yet, and the
/// token is the entire credential. The org and role come from the stored invitation, never from
/// this body.
/// </summary>
public class AcceptInvitationRequest
{
    public string Token { get; set; } = string.Empty;

    /// <summary>Chosen by the invitee, so no administrator ever knows it. Subject to the full password policy.</summary>
    public string Password { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}
