using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Covers the auth hardening: refresh-token rotation and revocation, the strengthened password
/// policy, and the Auth:AllowSelfRegistration switch.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuthHardeningTests
{
    private readonly AgentHostApiFactory _factory;

    public AuthHardeningTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task LoginAndRegister_ReturnAShortLivedAccessTokenAndARefreshToken()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);

        Assert.False(string.IsNullOrWhiteSpace(registered.Token));
        Assert.False(string.IsNullOrWhiteSpace(registered.RefreshToken));
        Assert.NotEqual(registered.Token, registered.RefreshToken);

        // Short by design — revocation is handled by the refresh token, not by the access token.
        Assert.InRange(registered.ExpiresInSeconds, 1, 60 * 60);

        var loginResponse = await client.PostJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = registered.User.Email,
            Password = TestData.DefaultPassword,
        });
        Assert.Equal(HttpStatusCode.OK, loginResponse.StatusCode);
        var login = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.False(string.IsNullOrWhiteSpace(login!.RefreshToken));
        Assert.NotEqual(registered.RefreshToken, login.RefreshToken);
    }

    [Fact]
    public async Task Refresh_RotatesThePair_AndTheUsedTokenIsRejectedAfterwards()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);

        var refreshResponse = await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = registered.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshed = await refreshResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.NotNull(refreshed);
        Assert.NotEqual(registered.RefreshToken, refreshed!.RefreshToken);
        Assert.Equal(registered.User.Id, refreshed.User.Id);

        // The new access token really works.
        var authed = TestData.AuthedClient(_factory, refreshed.Token);
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/api/auth/me")).StatusCode);

        // The old refresh token is now dead (rotation), so replaying it fails.
        var replayResponse = await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = registered.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replayResponse.StatusCode);
    }

    [Fact]
    public async Task Refresh_WithAnUnknownToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = $"not-a-real-refresh-token-{TestData.Suffix()}" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_RevokesTheRefreshToken_SoItCannotMintNewAccessTokens()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);
        var authed = TestData.AuthedClient(_factory, registered.Token);

        var logoutResponse = await authed.PostJsonAsync("/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.OK, logoutResponse.StatusCode);

        var afterLogout = await client.PostJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = registered.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    [Fact]
    public async Task Logout_WithoutAToken_Returns401()
    {
        var client = _factory.CreateClient();
        var response = await client.PostJsonAsync("/api/auth/logout", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("short11")]          // under 12 characters
    [InlineData("elevenchars")]      // 11 characters, just under the bar
    [InlineData("aaaaaaaaaaaaaaaa")] // all one character
    [InlineData("password1234")]     // on the common-password blocklist
    public async Task Register_WithAWeakPassword_Returns400(string weakPassword)
    {
        var client = _factory.CreateClient();
        var suffix = TestData.Suffix();

        var response = await client.PostJsonAsync("/api/auth/register", new RegisterRequest
        {
            OrgName = $"Weak {suffix}",
            OrgSlug = $"weak-{suffix}",
            Email = $"weak-{suffix}@example.com",
            Password = weakPassword,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CreateUser_WithAWeakPassword_Returns400()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, owner.Token);

        var response = await client.PostJsonAsync("/api/users", new CreateUserRequest
        {
            Email = $"weak-member-{suffix}@example.com",
            Password = "password123",
            Role = AgentHost.Api.Domain.UserRole.Viewer,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// With Auth:AllowSelfRegistration disabled, anonymous signup is refused outright rather than
    /// silently creating a tenant. Uses a private host so the shared factory's config is untouched.
    /// </summary>
    [Fact]
    public async Task Register_WhenSelfRegistrationDisabled_Returns403()
    {
        await using var factory = new SelfRegistrationDisabledFactory();
        var client = factory.CreateClient();
        var suffix = TestData.Suffix();

        var response = await client.PostJsonAsync("/api/auth/register", new RegisterRequest
        {
            OrgName = $"Blocked {suffix}",
            OrgSlug = $"blocked-{suffix}",
            Email = $"blocked-{suffix}@example.com",
            Password = TestData.DefaultPassword,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        // Login still works for users provisioned out of band, so the switch only closes signup.
        var loginResponse = await client.PostJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = $"nobody-{suffix}@example.com",
            Password = TestData.DefaultPassword,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, loginResponse.StatusCode);
    }

    private sealed class SelfRegistrationDisabledFactory : AgentHostApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(
                new Dictionary<string, string?> { ["Auth:AllowSelfRegistration"] = "false" }));
        }
    }
}
