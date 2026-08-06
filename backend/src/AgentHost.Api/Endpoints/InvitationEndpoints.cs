using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Organization invitations — the path that lets someone join an existing org while choosing their
/// own password. See <see cref="InvitationService"/> for the security properties.
///
/// All three management endpoints require Maintainer+ and operate purely inside the caller's own
/// organization; only the acceptance endpoint is anonymous, because the invitee has no account yet.
/// </summary>
public static class InvitationEndpoints
{
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder app)
    {
        var invitationsApi = app.MapGroup("/api/invitations").WithTags("Invitations").RequireAuthorization();

        invitationsApi.MapPost("/", CreateInvitation).WithName("CreateInvitation")
            .WithValidation<CreateInvitationRequest>()
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        invitationsApi.MapGet("/", ListInvitations).WithName("ListInvitations")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        invitationsApi.MapDelete("/{id}", RevokeInvitation).WithName("RevokeInvitation")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);

        invitationsApi.MapPost("/accept", AcceptInvitation).WithName("AcceptInvitation")
            .WithValidation<AcceptInvitationRequest>()
            .AllowAnonymous();

        return app;
    }

    /// <summary>
    /// Creates an invitation for an email + role in the *caller's own* organization and queues the
    /// invitation email to that address.
    ///
    /// <b>Where the token goes depends on <c>Email:Provider</c>.</b> With a mailer configured the
    /// invitee receives the link and the response carries no token at all. With no mailer (the
    /// default) the raw token is returned here exactly once — it is then the only delivery channel
    /// there is, and passing it to the invitee over a trusted out-of-band channel is the inviter's
    /// job. Either way the server stores only a SHA-256 hash, so nothing can be shown again: a lost
    /// invitation means revoking it and creating a new one. See
    /// <see cref="InvitationService.CreateAsync"/>.
    ///
    /// 409 when the email already belongs to a user or already has an outstanding invitation.
    /// </summary>
    private static async Task<IResult> CreateInvitation(
        CreateInvitationRequest req, IInvitationService invitationService, ICallerContext caller, CancellationToken ct)
    {
        try
        {
            var result = await invitationService.CreateAsync(req, caller.OrgId, caller.UserId, ct);
            return Results.Created($"/api/invitations/{result.Invitation.Id}", result);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Lists the caller's organization's invitations — metadata only. Neither the raw token nor its
    /// hash is ever included, so this endpoint cannot be used to redeem anything.
    /// </summary>
    private static async Task<IResult> ListInvitations(
        IInvitationService invitationService, ICallerContext caller, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var invitations = await invitationService.ListAsync(caller.OrgId, ct);
        return Results.Ok(invitations.Select(i => InvitationResponse.From(i, now)).ToList());
    }

    /// <summary>
    /// Revokes a pending invitation in the caller's org. 404 for an unknown id, another tenant's
    /// invitation, or one that is already accepted/revoked.
    /// </summary>
    private static async Task<IResult> RevokeInvitation(
        string id, IInvitationService invitationService, ICallerContext caller, CancellationToken ct)
    {
        var revoked = await invitationService.RevokeAsync(id, caller.OrgId, ct);
        return revoked ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Redeems an invitation token: creates the user in the invitation's organization at the
    /// invitation's role, marks the invitation spent, and returns the same access+refresh pair a
    /// login would. Anonymous, because the invitee has no account yet.
    ///
    /// Unknown, expired, revoked and already-used tokens all produce the same flat 400 with no
    /// detail. That is deliberate: any distinction between them would turn this endpoint into an
    /// oracle for probing token validity.
    /// </summary>
    private static async Task<IResult> AcceptInvitation(
        AcceptInvitationRequest req, IInvitationService invitationService, CancellationToken ct)
    {
        var result = await invitationService.AcceptAsync(req, ct);
        return result is not null
            ? Results.Created($"/api/users/{result.User.Id}", result)
            : Results.BadRequest(new { error = "Invitation token is invalid, expired or already used" });
    }
}
