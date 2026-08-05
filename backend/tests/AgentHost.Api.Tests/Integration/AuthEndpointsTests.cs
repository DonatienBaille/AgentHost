using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class AuthEndpointsTests
{
    private readonly AgentHostApiFactory _factory;

    public AuthEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_WithNewOrgAndEmail_Returns201WithTokenAndUser()
    {
        var client = _factory.CreateClient();
        var suffix = TestData.Suffix();

        var response = await client.PostJsonAsync("/api/auth/register", new RegisterRequest
        {
            OrgName = $"Acme {suffix}",
            OrgSlug = $"acme-{suffix}",
            Email = $"founder-{suffix}@example.com",
            Password = TestData.DefaultPassword,
            DisplayName = "Founder",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.Equal($"founder-{suffix}@example.com", body.User.Email);
        Assert.Equal(UserRole.Owner, body.User.Role);
        Assert.False(string.IsNullOrWhiteSpace(body.User.OrgId));
    }

    [Fact]
    public async Task Register_WithDuplicateOrgSlug_Returns409()
    {
        var client = _factory.CreateClient();
        var suffix = TestData.Suffix();
        var first = await TestData.RegisterAsync(client, suffix);

        var dupSlugReq = new RegisterRequest
        {
            OrgName = "Another Org",
            OrgSlug = $"test-org-{suffix}", // same slug TestData.RegisterAsync used
            Email = $"someone-else-{TestData.Suffix()}@example.com",
            Password = TestData.DefaultPassword,
        };

        var response = await client.PostJsonAsync("/api/auth/register", dupSlugReq);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEmpty(first.Token); // sanity: first registration actually succeeded
    }

    [Fact]
    public async Task Register_WithDuplicateEmail_Returns409()
    {
        var client = _factory.CreateClient();
        var suffix = TestData.Suffix();
        var first = await TestData.RegisterAsync(client, suffix);

        var dupEmailReq = new RegisterRequest
        {
            OrgName = "Yet Another Org",
            OrgSlug = $"other-org-{TestData.Suffix()}",
            Email = first.User.Email, // same email as the first registration
            Password = TestData.DefaultPassword,
        };

        var response = await client.PostJsonAsync("/api/auth/register", dupEmailReq);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_Returns200WithToken()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);

        var response = await client.PostJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = registered.User.Email,
            Password = TestData.DefaultPassword,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Token));
        Assert.Equal(registered.User.Id, body.User.Id);
    }

    [Fact]
    public async Task Login_WithWrongPassword_Returns401()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);

        var response = await client.PostJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = registered.User.Email,
            Password = "definitely-the-wrong-password",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = $"nobody-{TestData.Suffix()}@example.com",
            Password = TestData.DefaultPassword,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithValidToken_Returns200MatchingRegisteredUser()
    {
        var client = _factory.CreateClient();
        var registered = await TestData.RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", registered.Token);

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var user = await response.Content.ReadFromJsonAsync<User>(TestJson.Options);
        Assert.NotNull(user);
        Assert.Equal(registered.User.Id, user!.Id);
        Assert.Equal(registered.User.Email, user.Email);
    }

    [Fact]
    public async Task Me_WithNoToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithGarbageToken_Returns401()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "this.is.not-a-valid-jwt");

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
