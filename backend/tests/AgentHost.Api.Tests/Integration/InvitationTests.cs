using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// The invitation flow: invite -> accept -> login, plus the properties that make it safe —
/// single-use tokens, org scoping (an invitation from org A must not land a user in org B),
/// metadata-only listings, revocation, role gating, and the same password policy as every other
/// place a password is set.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class InvitationTests
{
    private readonly AgentHostApiFactory _factory;

    public InvitationTests(AgentHostApiFactory factory) => _factory = factory;

    private static CreateInvitationRequest InviteFor(string email, UserRole role = UserRole.Developer) =>
        new() { Email = email, Role = role };

    private async Task<(HttpClient Owner, AuthResponse Auth, string Suffix)> OwnerAsync()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var auth = await TestData.RegisterAsync(bootstrap, suffix);
        return (TestData.AuthedClient(_factory, auth.Token), auth, suffix);
    }

    [Fact]
    public async Task Invite_Accept_ThenLogin_LandsTheUserInTheInvitingOrgAtTheInvitedRole()
    {
        var (owner, ownerAuth, suffix) = await OwnerAsync();
        var email = $"invitee-{suffix}@example.com";

        var createResponse = await owner.PostJsonAsync("/api/invitations", InviteFor(email, UserRole.Maintainer));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CreateInvitationResponse>(TestJson.Options);
        Assert.NotNull(created);
        Assert.False(string.IsNullOrWhiteSpace(created!.Token));
        Assert.Equal(email, created.Invitation.Email);
        Assert.Equal(UserRole.Maintainer, created.Invitation.Role);
        Assert.True(created.Invitation.Active);

        // Accepting is anonymous — the invitee has no account yet.
        var anonymous = _factory.CreateClient();
        var acceptResponse = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = created.Token!,
            Password = TestData.DefaultPassword,
            DisplayName = "Invited Person",
        });
        Assert.Equal(HttpStatusCode.Created, acceptResponse.StatusCode);
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.NotNull(accepted);

        // Same shape as a login: a usable access token plus a revocable refresh token.
        Assert.False(string.IsNullOrWhiteSpace(accepted!.Token));
        Assert.False(string.IsNullOrWhiteSpace(accepted.RefreshToken));
        Assert.Equal(ownerAuth.User.OrgId, accepted.User.OrgId);
        Assert.Equal(UserRole.Maintainer, accepted.User.Role);
        Assert.Equal(email, accepted.User.Email);

        // The returned access token really works.
        var invitee = TestData.AuthedClient(_factory, accepted.Token);
        Assert.Equal(HttpStatusCode.OK, (await invitee.GetAsync("/api/auth/me")).StatusCode);

        // And the invitee can now log in with the password *they* chose.
        var loginResponse = await anonymous.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = email, Password = TestData.DefaultPassword });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);

        // The listing now shows it as spent, and never carries a token.
        var listed = await owner.GetFromJsonAsync<List<InvitationResponse>>("/api/invitations", TestJson.Options);
        var mine = Assert.Single(listed!, i => i.Id == created.Invitation.Id);
        Assert.NotNull(mine.AcceptedAt);
        Assert.False(mine.Active);
    }

    [Fact]
    public async Task InvitationToken_IsSingleUse()
    {
        var (owner, _, suffix) = await OwnerAsync();
        var created = await CreateInvitationAsync(owner, $"single-use-{suffix}@example.com");

        var anonymous = _factory.CreateClient();
        var first = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = created.Token!,
            Password = TestData.DefaultPassword,
        });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var replay = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = created.Token!,
            Password = TestData.DefaultPassword,
        });
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    /// <summary>
    /// The org an accepted invitation lands the user in comes from the stored invitation row, not
    /// from anything the acceptor sends. Org B's owner holding org A's token cannot redirect it.
    /// </summary>
    [Fact]
    public async Task InvitationFromOrgA_CannotCreateAUserInOrgB()
    {
        var (ownerA, authA, suffixA) = await OwnerAsync();
        var (_, authB, _) = await OwnerAsync();
        Assert.NotEqual(authA.User.OrgId, authB.User.OrgId);

        var created = await CreateInvitationAsync(ownerA, $"cross-org-{suffixA}@example.com");

        var anonymous = _factory.CreateClient();
        var acceptResponse = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = created.Token!,
            Password = TestData.DefaultPassword,
        });
        acceptResponse.EnsureSuccessStatusCode();
        var accepted = await acceptResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);

        Assert.Equal(authA.User.OrgId, accepted!.User.OrgId);
        Assert.NotEqual(authB.User.OrgId, accepted.User.OrgId);
    }

    /// <summary>Listing is org-scoped: another tenant's invitations are simply not there.</summary>
    [Fact]
    public async Task ListInvitations_OnlyReturnsTheCallersOwnOrgs()
    {
        var (ownerA, _, suffixA) = await OwnerAsync();
        var (ownerB, _, _) = await OwnerAsync();

        var createdA = await CreateInvitationAsync(ownerA, $"scoped-{suffixA}@example.com");

        var seenByB = await ownerB.GetFromJsonAsync<List<InvitationResponse>>("/api/invitations", TestJson.Options);
        Assert.DoesNotContain(seenByB!, i => i.Id == createdA.Invitation.Id);

        // Nor can B revoke it — an unknown id and another tenant's id are both 404, never 403.
        Assert.Equal(HttpStatusCode.NotFound,
            (await ownerB.DeleteAsync($"/api/invitations/{createdA.Invitation.Id}")).StatusCode);
    }

    [Fact]
    public async Task RevokedInvitation_CannotBeAccepted()
    {
        var (owner, _, suffix) = await OwnerAsync();
        var created = await CreateInvitationAsync(owner, $"revoked-{suffix}@example.com");

        Assert.Equal(HttpStatusCode.NoContent,
            (await owner.DeleteAsync($"/api/invitations/{created.Invitation.Id}")).StatusCode);

        var anonymous = _factory.CreateClient();
        var acceptResponse = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = created.Token!,
            Password = TestData.DefaultPassword,
        });
        Assert.Equal(HttpStatusCode.BadRequest, acceptResponse.StatusCode);

        // Revoking twice is a 404, not a silent success.
        Assert.Equal(HttpStatusCode.NotFound,
            (await owner.DeleteAsync($"/api/invitations/{created.Invitation.Id}")).StatusCode);
    }

    [Fact]
    public async Task Accept_WithAnUnknownToken_Returns400_WithNoDetailAboutWhy()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = $"not-a-real-invitation-token-{TestData.Suffix()}",
            Password = TestData.DefaultPassword,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Unknown, expired, revoked and already-used must be indistinguishable, so the body says
        // all four at once rather than naming the one that actually applied.
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("invalid, expired or already used", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Invite_ForAnExistingUserOrADuplicateInvite_Returns409()
    {
        var (owner, ownerAuth, suffix) = await OwnerAsync();

        // Existing user: the owner's own address.
        var existing = await owner.PostJsonAsync("/api/invitations", InviteFor(ownerAuth.User.Email));
        Assert.Equal(HttpStatusCode.Conflict, existing.StatusCode);

        // Duplicate outstanding invitation.
        var email = $"dupe-{suffix}@example.com";
        Assert.Equal(HttpStatusCode.Created, (await owner.PostJsonAsync("/api/invitations", InviteFor(email))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await owner.PostJsonAsync("/api/invitations", InviteFor(email))).StatusCode);
    }

    [Fact]
    public async Task Invitations_RequireMaintainerOrAbove()
    {
        var (owner, _, suffix) = await OwnerAsync();
        var (developerToken, _) = await TestData.CreateUserWithRoleAsync(owner, UserRole.Developer, suffix);
        var developer = TestData.AuthedClient(_factory, developerToken);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await developer.PostJsonAsync("/api/invitations", InviteFor($"nope-{suffix}@example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await developer.GetAsync("/api/invitations")).StatusCode);

        // Anonymous callers get 401 on the management endpoints, but /accept is open.
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/invitations")).StatusCode);
    }

    [Theory]
    [InlineData("short11")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("password1234")]
    public async Task Accept_WithAWeakPassword_Returns400_AndLeavesTheInvitationRedeemable(string weakPassword)
    {
        var (owner, _, suffix) = await OwnerAsync();
        var created = await CreateInvitationAsync(owner, $"weak-invitee-{suffix}@example.com");

        var anonymous = _factory.CreateClient();
        var rejected = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = created.Token!,
            Password = weakPassword,
        });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        // Policy rejection must not consume the invitation.
        var accepted = await anonymous.PostJsonAsync("/api/invitations/accept", new AcceptInvitationRequest
        {
            Token = created.Token!,
            Password = TestData.DefaultPassword,
        });
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    private static async Task<CreateInvitationResponse> CreateInvitationAsync(
        HttpClient owner, string email, UserRole role = UserRole.Developer)
    {
        var response = await owner.PostJsonAsync("/api/invitations", InviteFor(email, role));
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<CreateInvitationResponse>(TestJson.Options);
        return created ?? throw new InvalidOperationException("CreateInvitation did not return a body");
    }
}
