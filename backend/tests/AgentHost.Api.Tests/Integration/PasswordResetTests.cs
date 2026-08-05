using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Password reset (anonymous, token-based) and self-service password change.
///
/// The reset flow needs the raw token, which a production instance deliberately never returns, so
/// the token-driven tests run against their own host with Auth:ReturnResetTokenInResponse on — and
/// one test pins the fact that the shared (default-configured) host does NOT return it.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class PasswordResetTests
{
    private const string NewPassword = "a-brand-new-passphrase-42";

    private readonly AgentHostApiFactory _factory;

    public PasswordResetTests(AgentHostApiFactory factory) => _factory = factory;

    /// <summary>Host that opts into the dev-only "return the reset token in the response" behaviour.</summary>
    private sealed class ResetTokenInResponseFactory : AgentHostApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Auth:ReturnResetTokenInResponse"] = "true" }));
        }
    }

    [Fact]
    public async Task RequestReset_Returns202_ForBothKnownAndUnknownEmails()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);

        var known = await client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = registered.User.Email });
        var unknown = await client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = $"nobody-{TestData.Suffix()}@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);

        // Identical bodies: nothing here distinguishes an account that exists from one that does not.
        var knownBody = await known.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        var unknownBody = await unknown.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        Assert.Equal(knownBody!.Message, unknownBody!.Message);

        // Default configuration: the token is never handed to an anonymous caller.
        Assert.Null(knownBody.Token);
        Assert.Null(unknownBody.Token);
    }

    [Fact]
    public async Task ResetConfirm_SetsTheNewPassword_AndEndsExistingSessions()
    {
        await using var factory = new ResetTokenInResponseFactory();
        var client = factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);

        // A second live session, to prove the reset ends sessions it did not initiate.
        var secondLogin = await client.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = registered.User.Email, Password = TestData.DefaultPassword });
        secondLogin.EnsureSuccessStatusCode();
        var second = await secondLogin.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);

        var token = await RequestTokenAsync(client, registered.User.Email);
        Assert.False(string.IsNullOrWhiteSpace(token));

        var confirm = await client.PostJsonAsync("/api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest { Token = token, NewPassword = NewPassword });
        Assert.Equal(HttpStatusCode.NoContent, confirm.StatusCode);

        // Old password no longer works; the new one does.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = registered.User.Email, Password = TestData.DefaultPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = registered.User.Email, Password = NewPassword })).StatusCode);

        // Both pre-reset sessions are dead: neither refresh token can mint a new access token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = registered.RefreshToken })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = second!.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task ResetToken_IsSingleUse_AndSupersededByANewerRequest()
    {
        await using var factory = new ResetTokenInResponseFactory();
        var client = factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);

        var first = await RequestTokenAsync(client, registered.User.Email);

        // A second request supersedes the first, so the older token is already dead.
        var secondToken = await RequestTokenAsync(client, registered.User.Email);
        Assert.NotEqual(first, secondToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest { Token = first, NewPassword = NewPassword })).StatusCode);

        // The newest one works exactly once.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostJsonAsync("/api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest { Token = secondToken, NewPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest { Token = secondToken, NewPassword = "yet-another-passphrase-9" })).StatusCode);
    }

    [Fact]
    public async Task ResetConfirm_WithAnUnknownToken_Returns400()
    {
        var client = _factory.CreateClient();

        var response = await client.PostJsonAsync("/api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest { Token = $"nope-{TestData.Suffix()}", NewPassword = NewPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChangePassword_VerifiesTheCurrentOne_AndRevokesOtherSessions()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);

        var otherLogin = await client.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = registered.User.Email, Password = TestData.DefaultPassword });
        var other = await otherLogin.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);

        var authed = TestData.AuthedClient(_factory, registered.Token);

        // Wrong current password is refused outright.
        Assert.Equal(HttpStatusCode.BadRequest, (await authed.PostJsonAsync("/api/auth/password",
            new ChangePasswordRequest { CurrentPassword = "not-the-current-password", NewPassword = NewPassword })).StatusCode);

        var changeResponse = await authed.PostJsonAsync("/api/auth/password",
            new ChangePasswordRequest { CurrentPassword = TestData.DefaultPassword, NewPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);

        // The caller gets a fresh, working pair so its own session survives...
        var changed = await changeResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.False(string.IsNullOrWhiteSpace(changed!.RefreshToken));
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = changed.RefreshToken })).StatusCode);

        // ...while the other session is logged out.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = other!.RefreshToken })).StatusCode);

        // And the new password is what logs in now.
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync("/api/auth/login",
            new LoginRequest { Email = registered.User.Email, Password = NewPassword })).StatusCode);
    }

    [Fact]
    public async Task ChangePassword_WithoutAToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostJsonAsync("/api/auth/password",
            new ChangePasswordRequest { CurrentPassword = TestData.DefaultPassword, NewPassword = NewPassword });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("short11")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("password1234")]
    public async Task ChangePassword_WithAWeakNewPassword_Returns400(string weakPassword)
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);
        var authed = TestData.AuthedClient(_factory, registered.Token);

        var response = await authed.PostJsonAsync("/api/auth/password",
            new ChangePasswordRequest { CurrentPassword = TestData.DefaultPassword, NewPassword = weakPassword });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("short11")]
    [InlineData("aaaaaaaaaaaaaaaa")]
    [InlineData("password1234")]
    public async Task ResetConfirm_WithAWeakNewPassword_Returns400_AndDoesNotConsumeTheToken(string weakPassword)
    {
        await using var factory = new ResetTokenInResponseFactory();
        var client = factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);
        var token = await RequestTokenAsync(client, registered.User.Email);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync("/api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest { Token = token, NewPassword = weakPassword })).StatusCode);

        // A policy rejection must not burn the token.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostJsonAsync("/api/auth/password-reset/confirm",
            new PasswordResetConfirmRequest { Token = token, NewPassword = NewPassword })).StatusCode);
    }

    private static async Task<string> RequestTokenAsync(HttpClient client, string email)
    {
        var response = await client.PostJsonAsync("/api/auth/password-reset/request",
            new PasswordResetRequestRequest { Email = email });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<PasswordResetRequestResponse>(TestJson.Options);
        return body?.Token ?? throw new InvalidOperationException(
            "Auth:ReturnResetTokenInResponse should be enabled on this host");
    }
}
