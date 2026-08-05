using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// One create -&gt; update -&gt; verify -&gt; delete -&gt; verify-gone round trip per resource for the
/// CRUD endpoints on organizations, users, agents and secrets. Each repository's actual delete
/// semantics is matched rather than assumed: organizations/users/agents/secrets use soft delete
/// (deleted_at, filtered out of every read), which these tests confirm by asserting GET returns
/// 404 after DELETE. (Webhooks use a hard DELETE and get their own CRUD coverage in
/// WebhookEndpointsTests, alongside the dispatch-signature test.)
///
/// Note the organization round trip operates on the caller's *own* org: reads, updates and deletes
/// are scoped to the organization in the caller's JWT, so an org they merely created (and cannot
/// authenticate into) is deliberately invisible to them — asserted below.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class CrudEndpointsTests
{
    private readonly AgentHostApiFactory _factory;

    public CrudEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Organization_UpdateDeleteOwnOrg_RoundTrips()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, owner.Token);
        var ownOrgId = owner.User.OrgId;

        // An owner may still provision an additional organization...
        var createResponse = await client.PostJsonAsync("/api/organizations", new CreateOrganizationRequest
        {
            Name = $"Crud Org {suffix}",
            Slug = $"crud-org-{suffix}",
            Plan = "free",
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var otherOrg = await createResponse.Content.ReadFromJsonAsync<Organization>(TestJson.Options);
        Assert.NotNull(otherOrg);

        // ...but their token is scoped to their own org, so the new one is not readable by them.
        var otherGetResponse = await client.GetAsync($"/api/organizations/{otherOrg!.Id}");
        Assert.Equal(HttpStatusCode.NotFound, otherGetResponse.StatusCode);

        // GET / returns exactly the caller's own organization, never the whole tenant roster.
        var listResponse = await client.GetAsync("/api/organizations");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<Organization>>(TestJson.Options);
        Assert.NotNull(list);
        Assert.Equal(ownOrgId, Assert.Single(list!).Id);

        var updateResponse = await client.PutJsonAsync($"/api/organizations/{ownOrgId}", new UpdateOrganizationRequest
        {
            Name = $"Crud Org {suffix} Renamed",
            Plan = "team",
        });
        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        var updated = await updateResponse.Content.ReadFromJsonAsync<Organization>(TestJson.Options);
        Assert.Equal($"Crud Org {suffix} Renamed", updated!.Name);
        Assert.Equal("team", updated.Plan);

        var getResponse = await client.GetAsync($"/api/organizations/{ownOrgId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<Organization>(TestJson.Options);
        Assert.Equal($"Crud Org {suffix} Renamed", fetched!.Name);
        Assert.Equal("team", fetched.Plan);

        var deleteResponse = await client.DeleteAsync($"/api/organizations/{ownOrgId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var afterDeleteResponse = await client.GetAsync($"/api/organizations/{ownOrgId}");
        Assert.Equal(HttpStatusCode.NotFound, afterDeleteResponse.StatusCode);
    }

    /// <summary>
    /// Deleting an organization must cascade: its projects/agents/runs stop being readable too,
    /// rather than being orphaned but still live (the GDPR-erasure gap this replaces).
    /// </summary>
    [Fact]
    public async Task DeleteOrganization_CascadesToProjectsAgentsAndRuns()
    {
        var suffix = TestData.Suffix();
        var (client, owner, project, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var runResponse = await client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = agent.Id });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<Run>(TestJson.Options);
        Assert.NotNull(run);

        // Sanity: everything is readable before the delete.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/projects/{project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/agents/{agent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/runs/{run!.Id}")).StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/organizations/{owner.User.OrgId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/agents/{agent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/runs/{run.Id}")).StatusCode);
    }

    /// <summary>Deleting a project cascades to the agents and runs beneath it.</summary>
    [Fact]
    public async Task DeleteProject_CascadesToAgentsAndRuns()
    {
        var suffix = TestData.Suffix();
        var (client, _, project, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var runResponse = await client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = agent.Id });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<Run>(TestJson.Options);
        Assert.NotNull(run);

        var deleteResponse = await client.DeleteAsync($"/api/projects/{project.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/projects/{project.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/agents/{agent.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/runs/{run!.Id}")).StatusCode);
    }

    [Fact]
    public async Task User_CreateUpdateDelete_RoundTrips()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, owner.Token);

        // CreateUserRequest no longer carries an OrgId — the new user lands in the caller's org.
        var createResponse = await client.PostJsonAsync("/api/users", new CreateUserRequest
        {
            Email = $"crud-user-{suffix}@example.com",
            Password = TestData.DefaultPassword,
            DisplayName = "Original Name",
            Role = UserRole.Viewer,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var user = await createResponse.Content.ReadFromJsonAsync<User>(TestJson.Options);
        Assert.NotNull(user);
        Assert.Equal(UserRole.Viewer, user!.Role);
        Assert.Equal(owner.User.OrgId, user.OrgId);

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
    /// Full secret CRUD, including the GET list/by-id routes the admin UI needs. The critical
    /// assertion is negative: no response on any route may carry the plaintext or the ciphertext.
    /// </summary>
    [Fact]
    public async Task Secret_CreateListGetRotateDelete_NeverExposesValue()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var owner = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, owner.Token);

        const string plaintext = "super-secret-plaintext-value";
        var createResponse = await client.PostJsonAsync("/api/secrets", new CreateSecretRequest
        {
            Name = $"crud-secret-{suffix}",
            Value = plaintext,
            Scope = SecretScope.Org,
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        var createBody = await createResponse.Content.ReadAsStringAsync();
        AssertNoSecretMaterial(createBody, plaintext);
        using var doc = JsonDocument.Parse(createBody);
        var secretId = doc.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(secretId));

        // GET /api/secrets — the list endpoint the Angular admin page calls (previously a 404).
        var listResponse = await client.GetAsync("/api/secrets");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var listBody = await listResponse.Content.ReadAsStringAsync();
        AssertNoSecretMaterial(listBody, plaintext);
        var list = await listResponse.Content.ReadFromJsonAsync<List<SecretResponse>>(TestJson.Options);
        Assert.NotNull(list);
        Assert.Contains(list!, s => s.Id == secretId);
        Assert.All(list!, s => Assert.Equal(owner.User.OrgId, s.OrgId));

        // GET /api/secrets/{id} — metadata only.
        var getResponse = await client.GetAsync($"/api/secrets/{secretId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        AssertNoSecretMaterial(await getResponse.Content.ReadAsStringAsync(), plaintext);

        // PUT rotates the stored value without ever echoing it.
        const string rotated = "rotated-plaintext-value";
        var rotateResponse = await client.PutJsonAsync($"/api/secrets/{secretId}", new UpdateSecretRequest { Value = rotated });
        Assert.Equal(HttpStatusCode.OK, rotateResponse.StatusCode);
        AssertNoSecretMaterial(await rotateResponse.Content.ReadAsStringAsync(), rotated);

        var deleteResponse = await client.DeleteAsync($"/api/secrets/{secretId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/secrets/{secretId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/secrets/{secretId}")).StatusCode);
    }

    private static void AssertNoSecretMaterial(string body, string plaintext)
    {
        Assert.DoesNotContain(plaintext, body, StringComparison.Ordinal);
        Assert.DoesNotContain("encryptedValue", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vaultPath", body, StringComparison.OrdinalIgnoreCase);
    }
}
