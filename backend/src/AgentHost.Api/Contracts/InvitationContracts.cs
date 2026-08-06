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
/// The response to POST /api/invitations. The server keeps only a SHA-256 hash of the token, so
/// whatever <see cref="Token"/> carries here is unrecoverable once the response is discarded — a
/// lost invitation means revoking it and issuing a new one.
/// </summary>
public class CreateInvitationResponse
{
    public InvitationResponse Invitation { get; set; } = null!;

    /// <summary>
    /// The raw invitation token, shown exactly once — and <b>only when this deployment has no
    /// mailer</b> (<c>Email:Provider</c> = none, the default), where handing it to the inviter for
    /// out-of-band delivery is the only thing that makes invitations work at all.
    ///
    /// Null once a mailer is configured: the token then goes straight to the invitee by email, and
    /// echoing it back would leave a credential capable of creating an account under someone
    /// else's address sitting in the inviter's browser, logs and proxies for no purpose. See
    /// <see cref="Services.InvitationService.CreateAsync"/>.
    /// </summary>
    public string? Token { get; set; }
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
