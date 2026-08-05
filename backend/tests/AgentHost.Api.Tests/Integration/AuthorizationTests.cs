using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// RBAC coverage for role-gated endpoints (owner &gt; maintainer &gt; developer &gt; viewer, per
/// AuthorizationPolicies): no token -&gt; 401, an authenticated-but-underprivileged role -&gt; 403,
/// a sufficient role -&gt; success.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AuthorizationTests
{
    private readonly AgentHostApiFactory _factory;

    public AuthorizationTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task CreateOrganization_RequiresOwner()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var ownerClient = TestData.AuthedClient(_factory, owner.Token);

        var (maintainerToken, _) = await TestData.CreateUserWithRoleAsync(ownerClient, owner.User.OrgId, UserRole.Maintainer, suffix);
        var maintainerClient = TestData.AuthedClient(_factory, maintainerToken);

        var anonClient = _factory.CreateClient();

        var newOrgReq = new CreateOrganizationRequest
        {
            Name = $"RBAC Org {suffix}",
            Slug = $"rbac-org-{suffix}",
            Plan = "free",
        };

        // No token -> 401.
        var anonResponse = await anonClient.PostJsonAsync("/api/organizations", newOrgReq);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);

        // Maintainer is below Owner -> 403.
        var maintainerResponse = await maintainerClient.PostJsonAsync("/api/organizations", newOrgReq);
        Assert.Equal(HttpStatusCode.Forbidden, maintainerResponse.StatusCode);

        // Owner -> 201.
        var ownerResponse = await ownerClient.PostJsonAsync("/api/organizations", newOrgReq);
        Assert.Equal(HttpStatusCode.Created, ownerResponse.StatusCode);
    }

    [Fact]
    public async Task CreateUser_RequiresMaintainer()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var ownerClient = TestData.AuthedClient(_factory, owner.Token);

        var (developerToken, _) = await TestData.CreateUserWithRoleAsync(ownerClient, owner.User.OrgId, UserRole.Developer, suffix);
        var developerClient = TestData.AuthedClient(_factory, developerToken);

        var anonClient = _factory.CreateClient();

        CreateUserRequest NewUserReq(string tag) => new()
        {
            OrgId = owner.User.OrgId,
            Email = $"rbac-{tag}-{suffix}@example.com",
            Password = TestData.DefaultPassword,
            Role = UserRole.Viewer,
        };

        // No token -> 401.
        var anonResponse = await anonClient.PostJsonAsync("/api/users", NewUserReq("anon"));
        Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);

        // Developer is below Maintainer -> 403.
        var developerResponse = await developerClient.PostJsonAsync("/api/users", NewUserReq("dev"));
        Assert.Equal(HttpStatusCode.Forbidden, developerResponse.StatusCode);

        // Owner (>= Maintainer) -> 201.
        var ownerResponse = await ownerClient.PostJsonAsync("/api/users", NewUserReq("owner"));
        Assert.Equal(HttpStatusCode.Created, ownerResponse.StatusCode);
    }

    [Fact]
    public async Task CreateRun_RequiresDeveloper()
    {
        var suffix = TestData.Suffix();
        var (ownerClient, owner, _, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var (viewerToken, _) = await TestData.CreateUserWithRoleAsync(ownerClient, owner.User.OrgId, UserRole.Viewer, suffix);
        var viewerClient = TestData.AuthedClient(_factory, viewerToken);

        var anonClient = _factory.CreateClient();

        var runReq = new CreateRunRequest { AgentId = agent.Id };

        // No token -> 401.
        var anonResponse = await anonClient.PostJsonAsync("/api/runs", runReq);
        Assert.Equal(HttpStatusCode.Unauthorized, anonResponse.StatusCode);

        // Viewer is below Developer -> 403.
        var viewerResponse = await viewerClient.PostJsonAsync("/api/runs", runReq);
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);

        // Owner (>= Developer) -> 201.
        var ownerResponse = await ownerClient.PostJsonAsync("/api/runs", runReq);
        Assert.Equal(HttpStatusCode.Created, ownerResponse.StatusCode);
    }
}
