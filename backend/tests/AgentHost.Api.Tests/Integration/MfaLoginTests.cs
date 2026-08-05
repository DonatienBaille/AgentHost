using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// The MFA login step-up, end to end: enroll, confirm, and then a login that no longer hands out a
/// session for a correct password alone.
///
/// Two assertions here are the ones that decide whether this feature is real or theatre:
/// <see cref="MfaChallengeToken_IsNotUsableAsAnAccessToken"/> (a challenge token must not
/// authenticate anything) and <see cref="RecoveryCode_CannotBeReplayed"/>.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class MfaLoginTests
{
    private readonly AgentHostApiFactory _factory;

    public MfaLoginTests(AgentHostApiFactory factory) => _factory = factory;

    /// <summary>A registered user with MFA enrolled and confirmed, plus everything needed to drive it.</summary>
    private sealed record MfaUser(AuthResponse Auth, byte[] Secret, List<string> RecoveryCodes)
    {
        public string Email => Auth.User.Email;
        public string CurrentCode => Totp.ComputeCodeAt(Secret, DateTimeOffset.UtcNow);
    }

    private async Task<MfaUser> EnrollAndConfirmAsync()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);
        var authed = TestData.AuthedClient(_factory, registered.Token);

        var enrollResponse = await authed.PostJsonAsync("/api/auth/mfa/enroll", new { });
        enrollResponse.EnsureSuccessStatusCode();
        var enroll = await enrollResponse.Content.ReadFromJsonAsync<MfaEnrollResponse>(TestJson.Options);
        Assert.NotNull(enroll);
        Assert.False(string.IsNullOrWhiteSpace(enroll!.Secret));
        Assert.Contains("otpauth://totp/", enroll.OtpAuthUri);

        var secret = Base32.Decode(enroll.Secret);

        var confirmResponse = await authed.PostJsonAsync("/api/auth/mfa/confirm",
            new MfaConfirmRequest { Code = Totp.ComputeCodeAt(secret, DateTimeOffset.UtcNow) });
        confirmResponse.EnsureSuccessStatusCode();
        var confirm = await confirmResponse.Content.ReadFromJsonAsync<MfaConfirmResponse>(TestJson.Options);
        Assert.True(confirm!.Enabled);
        Assert.NotEmpty(confirm.RecoveryCodes);

        return new MfaUser(registered, secret, confirm.RecoveryCodes);
    }

    private async Task<LoginResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>(TestJson.Options);
        return login!;
    }

    [Fact]
    public async Task Enrollment_IsNotActiveUntilConfirmed()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);
        var authed = TestData.AuthedClient(_factory, registered.Token);

        var enrollResponse = await authed.PostJsonAsync("/api/auth/mfa/enroll", new { });
        enrollResponse.EnsureSuccessStatusCode();

        // Enrolled but unconfirmed: login still yields a normal session, so a mis-scanned QR code
        // cannot lock the user out of their own account.
        var login = await LoginAsync(client, registered.User.Email, TestData.DefaultPassword);
        Assert.False(login.MfaRequired);
        Assert.False(string.IsNullOrWhiteSpace(login.Token));

        // And a wrong code does not confirm it.
        var badConfirm = await authed.PostJsonAsync("/api/auth/mfa/confirm", new MfaConfirmRequest { Code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, badConfirm.StatusCode);
    }

    [Fact]
    public async Task Login_WithMfaEnabled_ReturnsAChallengeInsteadOfASession()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();

        var login = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);

        // The password was correct, and it bought no session at all.
        Assert.True(login.MfaRequired);
        Assert.False(string.IsNullOrWhiteSpace(login.MfaToken));
        Assert.True(string.IsNullOrEmpty(login.Token));
        Assert.True(string.IsNullOrEmpty(login.RefreshToken));
        Assert.Null(login.User);

        // Exchanging the challenge for a real pair.
        var verifyResponse = await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = login.MfaToken!, Code = mfaUser.CurrentCode });
        Assert.Equal(HttpStatusCode.OK, verifyResponse.StatusCode);
        var verified = await verifyResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);

        Assert.False(string.IsNullOrWhiteSpace(verified!.Token));
        Assert.False(string.IsNullOrWhiteSpace(verified.RefreshToken));
        Assert.Equal(mfaUser.Auth.User.Id, verified.User.Id);

        // The access token from /verify is a genuine session token.
        var authed = TestData.AuthedClient(_factory, verified.Token);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/auth/me")).StatusCode);
    }

    /// <summary>
    /// The assertion that decides whether MFA means anything: the challenge token handed out by
    /// login must not authenticate a single request. It carries a distinct audience and a
    /// token_type claim, so the bearer scheme refuses it.
    /// </summary>
    [Fact]
    public async Task MfaChallengeToken_IsNotUsableAsAnAccessToken()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();

        var login = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);
        Assert.True(login.MfaRequired);

        var withChallenge = _factory.CreateClient();
        withChallenge.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.MfaToken);

        // Not on the caller's own identity endpoint...
        Assert.Equal(HttpStatusCode.Unauthorized, (await withChallenge.GetAsync("/api/auth/me")).StatusCode);
        // ...nor on any ordinary authenticated endpoint...
        Assert.Equal(HttpStatusCode.Unauthorized, (await withChallenge.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withChallenge.GetAsync("/api/projects")).StatusCode);
        // ...nor to mint a fresh session by other means.
        Assert.Equal(HttpStatusCode.Unauthorized, (await withChallenge.PostJsonAsync("/api/auth/logout", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = login.MfaToken! })).StatusCode);
    }

    /// <summary>The separation runs both ways: a real access token is not a challenge token either.</summary>
    [Fact]
    public async Task AccessToken_IsNotUsableAsAnMfaChallengeToken()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();

        var response = await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = mfaUser.Auth.Token, Code = mfaUser.CurrentCode });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MfaVerify_WithAWrongCodeOrAGarbageToken_Returns401()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();
        var login = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = login.MfaToken!, Code = "000000" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = $"not-a-token-{TestData.Suffix()}", Code = mfaUser.CurrentCode })).StatusCode);
    }

    [Fact]
    public async Task RecoveryCode_CompletesALogin_AndCannotBeReplayed()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();
        var recoveryCode = mfaUser.RecoveryCodes[0];

        var firstLogin = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);
        var firstVerify = await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = firstLogin.MfaToken!, Code = recoveryCode });
        Assert.Equal(HttpStatusCode.OK, firstVerify.StatusCode);

        // Replay: a fresh challenge, the same recovery code. It has been consumed.
        var secondLogin = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);
        var replay = await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = secondLogin.MfaToken!, Code = recoveryCode });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // A different, unused recovery code still works.
        var thirdVerify = await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = secondLogin.MfaToken!, Code = mfaUser.RecoveryCodes[1] });
        Assert.Equal(HttpStatusCode.OK, thirdVerify.StatusCode);
    }

    [Fact]
    public async Task RecoveryCode_IsAcceptedRegardlessOfCasingAndDashes()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();

        var login = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);
        var mangled = mfaUser.RecoveryCodes[0].ToLowerInvariant().Replace("-", " ");

        var response = await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = login.MfaToken!, Code = mangled });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Disable_RequiresAValidCode_AndThenLoginStopsChallenging()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();

        // Get a real session first (the account now requires MFA to log in).
        var login = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);
        var verifyResponse = await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = login.MfaToken!, Code = mfaUser.CurrentCode });
        var session = await verifyResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        var authed = TestData.AuthedClient(_factory, session!.Token);

        // A session alone is not enough to strip the second factor.
        Assert.Equal(HttpStatusCode.BadRequest, (await authed.PostJsonAsync("/api/auth/mfa/disable",
            new MfaDisableRequest { Code = "000000" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await authed.PostJsonAsync("/api/auth/mfa/disable",
            new MfaDisableRequest { Code = mfaUser.CurrentCode })).StatusCode);

        // MFA is off: password alone is a full login again.
        var after = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);
        Assert.False(after.MfaRequired);
        Assert.False(string.IsNullOrWhiteSpace(after.Token));
    }

    [Fact]
    public async Task Disable_AcceptsARecoveryCode_ForALostAuthenticator()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();

        var login = await LoginAsync(client, mfaUser.Email, TestData.DefaultPassword);
        var verifyResponse = await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = login.MfaToken!, Code = mfaUser.RecoveryCodes[0] });
        var session = await verifyResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        var authed = TestData.AuthedClient(_factory, session!.Token);

        Assert.Equal(HttpStatusCode.NoContent, (await authed.PostJsonAsync("/api/auth/mfa/disable",
            new MfaDisableRequest { Code = mfaUser.RecoveryCodes[1] })).StatusCode);
    }

    [Fact]
    public async Task MfaEndpoints_RequireAuthentication_ExceptVerify()
    {
        var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/mfa/enroll", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/mfa/confirm",
            new MfaConfirmRequest { Code = "123456" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/mfa/disable",
            new MfaDisableRequest { Code = "123456" })).StatusCode);

        // /verify is anonymous by design — it is reached before any session exists — so an invalid
        // challenge is rejected on its merits (401 from the handler), not by the auth pipeline.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/mfa/verify",
            new MfaVerifyRequest { MfaToken = "nonsense", Code = "123456" })).StatusCode);
    }

    [Fact]
    public async Task Login_WithMfaEnabledAndAWrongPassword_StillReturns401_AndNoChallenge()
    {
        var mfaUser = await EnrollAndConfirmAsync();
        var client = _factory.CreateClient();

        var response = await client.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = mfaUser.Email, Password = "definitely-not-the-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
