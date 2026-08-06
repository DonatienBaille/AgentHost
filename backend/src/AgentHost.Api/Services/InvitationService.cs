using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services.Email;
using Serilog;

namespace AgentHost.Api.Services;

public interface IInvitationService
{
    /// <summary>
    /// Creates an invitation into <paramref name="orgId"/> and queues the invitation email. The
    /// raw token comes back in the response only when no mailer is configured — see
    /// <see cref="InvitationService.CreateAsync"/> for why.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The email already belongs to a user, or already has an outstanding invitation.
    /// </exception>
    Task<CreateInvitationResponse> CreateAsync(
        CreateInvitationRequest req, string orgId, string invitedByUserId, CancellationToken ct = default);

    Task<List<Invitation>> ListAsync(string orgId, CancellationToken ct = default);

    /// <summary>Revokes a pending invitation in the caller's org. False when unknown or already spent.</summary>
    Task<bool> RevokeAsync(string id, string orgId, CancellationToken ct = default);

    /// <summary>
    /// Redeems a token: creates the user in the invitation's org at the invitation's role and
    /// returns the same access+refresh pair as a login. Returns null for *any* failure — unknown,
    /// expired, revoked, already used — so the caller cannot tell them apart.
    /// </summary>
    Task<AuthResponse?> AcceptAsync(AcceptInvitationRequest req, CancellationToken ct = default);
}

/// <summary>
/// The invite flow: a maintainer/owner names an email and a role, and the invitee sets their own
/// password on acceptance. This is the only path that adds a member to an existing organization
/// without an administrator having chosen (and therefore known) their password.
///
/// Security properties worth stating explicitly:
///   * the target org is always the *inviter's* org, read from their token — an invitation can
///     never place a user in another tenant, whatever the request body says;
///   * only the token's SHA-256 hash is stored, so the invitations table hands out no credentials;
///   * acceptance is single-use, enforced by a conditional UPDATE rather than a read-then-write;
///   * every acceptance failure looks identical from outside (see <see cref="AcceptAsync"/>),
///     because distinguishing "expired" from "unknown" turns the endpoint into a token oracle.
/// </summary>
public class InvitationService : IInvitationService
{
    /// <summary>
    /// How long an invitation stays redeemable. Long enough for an out-of-band handover, short
    /// enough that a leaked token in a chat log stops being useful reasonably soon.
    /// </summary>
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromDays(7);

    private readonly IInvitationRepository _invitationRepository;
    private readonly IUserRepository _userRepository;
    private readonly IAuthTokenIssuer _tokenIssuer;
    private readonly IAuditService _auditService;
    private readonly IEmailDispatcher _emailDispatcher;
    private readonly EmailOptions _emailOptions;
    private readonly ILogger _logger;

    public InvitationService(
        IInvitationRepository invitationRepository,
        IUserRepository userRepository,
        IAuthTokenIssuer tokenIssuer,
        IAuditService auditService,
        IEmailDispatcher emailDispatcher,
        EmailOptions emailOptions,
        ILogger logger)
    {
        _invitationRepository = invitationRepository;
        _userRepository = userRepository;
        _tokenIssuer = tokenIssuer;
        _auditService = auditService;
        _emailDispatcher = emailDispatcher;
        _emailOptions = emailOptions;
        _logger = logger;
    }

    /// <summary>
    /// Mints the invitation, queues its email, and decides who gets to see the raw token.
    ///
    /// <b>Without a mailer</b> (<c>Email:Provider</c> = none, the default) the token is returned to
    /// the inviter, exactly as before this batch: it is then the only delivery channel there is,
    /// and removing it would break the one invitation path that works today.
    ///
    /// <b>With a mailer</b> it is not returned. The token is a bearer credential that creates an
    /// account bound to the invitee's address; once it reaches that address directly, echoing a
    /// second copy back to the inviter only widens exposure — it survives in the browser, in
    /// whatever the client logs, in intermediaries — and it lets the inviter accept the invitation
    /// in the invitee's name. Neither is needed for the flow to work, so neither is offered. A
    /// message that does not arrive is recovered by revoking the invitation and issuing a new one,
    /// which is also what a lost token has always required.
    ///
    /// The email is queued rather than awaited, like every other send in the system: a mail relay
    /// must not be able to make POST /api/invitations slow or make it fail after the invitation is
    /// already committed to the database.
    /// </summary>
    public async Task<CreateInvitationResponse> CreateAsync(
        CreateInvitationRequest req, string orgId, string invitedByUserId, CancellationToken ct = default)
    {
        var email = req.Email.Trim();

        if (await _userRepository.GetByEmailAsync(email, ct) is not null)
            throw new InvalidOperationException($"A user with email '{email}' already exists");
        if (await _invitationRepository.GetActiveByEmailAsync(email, ct) is not null)
            throw new InvalidOperationException($"An invitation for '{email}' is already outstanding");

        var rawToken = OpaqueToken.New();
        var now = DateTime.UtcNow;

        var invitation = new Invitation
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = orgId,
            Email = email,
            Role = req.Role,
            TokenHash = OpaqueToken.Hash(rawToken),
            InvitedByUserId = invitedByUserId,
            ExpiresAt = now.Add(InvitationLifetime),
            CreatedAt = now,
        };

        await _invitationRepository.InsertAsync(invitation, ct);
        await _auditService.RecordAsync(orgId, "invitation.created", invitedByUserId, "invitation", invitation.Id, ct: ct);

        _emailDispatcher.Enqueue(EmailTemplates.Invitation(
            email,
            _emailOptions.InvitationLink(rawToken),
            InvitationLifetime,
            _emailOptions.Language));

        var delivered = _emailDispatcher.IsConfigured;
        if (!delivered)
        {
            _logger.Information(
                "Invitation {InvitationId} créée sans mailer : le jeton brut est rendu à l'appelant, " +
                "qui doit le transmettre lui-même",
                invitation.Id);
        }

        return new CreateInvitationResponse
        {
            Invitation = InvitationResponse.From(invitation, now),
            // Voir le commentaire de la méthode : rendu seulement quand aucun autre canal n'existe.
            Token = delivered ? null : rawToken,
        };
    }

    public Task<List<Invitation>> ListAsync(string orgId, CancellationToken ct = default) =>
        _invitationRepository.ListByOrgAsync(orgId, ct);

    public async Task<bool> RevokeAsync(string id, string orgId, CancellationToken ct = default)
    {
        var revoked = await _invitationRepository.RevokeAsync(id, orgId, ct);
        if (revoked)
            await _auditService.RecordAsync(orgId, "invitation.revoked", null, "invitation", id, ct: ct);
        return revoked;
    }

    public async Task<AuthResponse?> AcceptAsync(AcceptInvitationRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Token)) return null;

        var invitation = await _invitationRepository.GetByHashAsync(OpaqueToken.Hash(req.Token), ct);
        if (invitation is null || !invitation.IsActive(DateTime.UtcNow))
            return null;

        // The email may have been claimed by another route (self-registration, POST /api/users)
        // between issue and acceptance. Refuse rather than colliding on users.email.
        if (await _userRepository.GetByEmailAsync(invitation.Email, ct) is not null)
        {
            _logger.Warning("Invitation {InvitationId} was accepted for an email that already has a user; refusing",
                invitation.Id);
            return null;
        }

        // Consume first. If this loses the race with a concurrent accept it returns false and we
        // stop here, so a token can never mint two users.
        if (!await _invitationRepository.MarkAcceptedAsync(invitation.Id, ct))
            return null;

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = UlidGenerator.NewUlid(),
            // From the stored invitation, never the request: this is what makes an invitation
            // issued by org A incapable of creating a user in org B.
            OrgId = invitation.OrgId,
            Email = invitation.Email,
            DisplayName = req.DisplayName,
            Role = invitation.Role,
            PasswordHash = PasswordHasher.Hash(req.Password),
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _userRepository.InsertAsync(user, ct);
        await _auditService.RecordAsync(invitation.OrgId, "invitation.accepted", user.Id, "invitation", invitation.Id, ct: ct);

        var (response, _) = await _tokenIssuer.IssueAsync(user, ct);
        return response;
    }
}
