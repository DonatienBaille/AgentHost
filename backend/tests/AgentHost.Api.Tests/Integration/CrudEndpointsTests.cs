using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// One create -&gt; update -&gt; verify -&gt; delete -&gt; verify-gone round trip per resource for the
/// newly-added CRUD endpoints on organizations, users, agents and secrets. Each repository's
/// actual delete semantics is matched rather than assumed: organizations/users/agents/secrets use
/// soft delete (deleted_at, filtered out of GetAsync), which these tests confirm by asserting
/// GET returns 404 after DELETE. (Webhooks use a hard DELETE and get their own CRUD coverage in
/// WebhookEndpointsTests, alongside the dispatch-signature test.)
/// </summary>
[Collection(IntegrationCollection.Name)]
public class CrudEndpointsTests
{
    private readonly AgentHostApiFactory _factory;

    public CrudEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Organization_CreateUpdateDelete_RoundTrips()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, owner.Token);

        var createResponse = await client.PostJsonAsync("/api/organizations", new CreateOrganizationRequest
        {
            Name = $"Crud Org {suffix}",
            Slug = $"crud-org-{suffix}",
            Plan = "free",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var org = await createResponse.Content.ReadFromJsonAsync<Organization>(TestJson.Options);
        Assert.NotNull(org);

        var updateResponse = await client.PutJsonAsync($"/api/organizations/{org!.Id}", new UpdateOrganizationRequest
        {
            Name = $"Crud Org {suffix} Renamed",
            Plan = "team",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<Organization>(TestJson.Options);
        Assert.Equal($"Crud Org {suffix} Renamed", updated!.Name);
        Assert.Equal("team", updated.Plan);

        var getResponse = await client.GetAsync($"/api/organizations/{org.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<Organization>(TestJson.Options);
        Assert.Equal($"Crud Org {suffix} Renamed", fetched!.Name);
        Assert.Equal("team", fetched.Plan);

        var deleteResponse = await client.DeleteAsync($"/api/organizations/{org.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDeleteResponse = await client.GetAsync($"/api/organizations/{org.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDeleteResponse.StatusCode);
    }

    [Fact]
    public async Task User_CreateUpdateDelete_RoundTrips()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, owner.Token);

        var createResponse = await client.PostJsonAsync("/api/users", new CreateUserRequest
        {
            OrgId = owner.User.OrgId,
            Email = $"crud-user-{suffix}@example.com",
            Password = TestData.DefaultPassword,
            DisplayName = "Original Name",
            Role = UserRole.Viewer,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var user = await createResponse.Content.ReadFromJsonAsync<User>(TestJson.Options);
        Assert.NotNull(user);
        Assert.Equal(UserRole.Viewer, user!.Role);

        var updateResponse = await client.PutJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest
        {
            DisplayName = "Updated Name",
            Role = UserRole.Developer,
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<User>(TestJson.Options);
        Assert.Equal("Updated Name", updated!.DisplayName);
        Assert.Equal(UserRole.Developer, updated.Role);

        var getResponse = await client.GetAsync($"/api/users/{user.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<User>(TestJson.Options);
        Assert.Equal("Updated Name", fetched!.DisplayName);
        Assert.Equal(UserRole.Developer, fetched.Role);

        var deleteResponse = await client.DeleteAsync($"/api/users/{user.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDeleteResponse = await client.GetAsync($"/api/users/{user.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDeleteResponse.StatusCode);
    }

    [Fact]
    public async Task Agent_CreateUpdateDelete_RoundTrips()
    {
        var suffix = TestData.Suffix();
        var (client, _, _, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var updateResponse = await client.PutJsonAsync($"/api/agents/{agent.Id}", new UpdateAgentRequest
        {
            Name = "Renamed Agent",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<Agent>(TestJson.Options);
        Assert.Equal("Renamed Agent", updated!.Name);

        var getResponse = await client.GetAsync($"/api/agents/{agent.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<Agent>(TestJson.Options);
        Assert.Equal("Renamed Agent", fetched!.Name);

        var deleteResponse = await client.DeleteAsync($"/api/agents/{agent.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDeleteResponse = await client.GetAsync($"/api/agents/{agent.Id}");
        Assert.Equal(HttpStatusCode.NotFound, afterDeleteResponse.StatusCode);
    }

    /// <summary>
    /// Secrets have no GET-by-id or PUT endpoint (SecretEndpoints only maps POST / and DELETE
    /// /{id}) — see final report. This confirms what IS there: create, then soft-delete via
    /// DELETE, then a second DELETE 404s because DeleteSecret's own GetAsync lookup now excludes
    /// the soft-deleted row (deleted_at IS NULL filter), which is the only externally observable
    /// evidence of the soft delete without a GET endpoint to check directly.
    /// </summary>
    [Fact]
    public async Task Secret_CreateThenDelete_SecondDeleteIsNotFound()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, owner.Token);

        var createResponse = await client.PostJsonAsync("/api/secrets", new CreateSecretRequest
        {
            OrgId = owner.User.OrgId,
            Name = $"crud-secret-{suffix}",
            Value = "super-secret-plaintext-value",
            Scope = SecretScope.Org,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var doc = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var secretId = doc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(secretId));

        // The create response must never echo the plaintext or ciphertext back.
        Assert.False(doc.RootElement.TryGetProperty("value", out _));
        Assert.False(doc.RootElement.TryGetProperty("encryptedValue", out _));

        var deleteResponse = await client.DeleteAsync($"/api/secrets/{secretId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var secondDeleteResponse = await client.DeleteAsync($"/api/secrets/{secretId}");
        Assert.Equal(HttpStatusCode.NotFound, secondDeleteResponse.StatusCode);
    }
}
