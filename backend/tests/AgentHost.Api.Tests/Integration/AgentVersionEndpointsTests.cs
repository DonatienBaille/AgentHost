using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

[Collection(IntegrationCollection.Name)]
public class AgentVersionEndpointsTests
{
    private readonly AgentHostApiFactory _factory;

    public AgentVersionEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task PublishVersion_ThenList_ThenAgentPointsAtNewestVersion()
    {
        var suffix = TestData.Suffix();
        var (client, _, _, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        // AgentService.CreateAsync already published version 1 as part of agent creation.
        var v1Response = await client.GetAsync($"/api/agents/{agent.Id}/versions");
        Assert.Equal(HttpStatusCode.OK, v1Response.StatusCode);
        var initialVersions = await v1Response.Content.ReadFromJsonAsync<List<AgentVersion>>(TestJson.Options);
        Assert.NotNull(initialVersions);
        Assert.Single(initialVersions!);
        Assert.Equal(1, initialVersions![0].VersionNumber);

        // Publish a second version with a valid manifest (metadata.name + spec required).
        var v2Manifest = TestData.BuildManifestYaml($"test-agent-{suffix}-v2");
        var publishReq = new PublishAgentVersionRequest { ManifestYaml = v2Manifest };

        var publishResponse = await client.PostJsonAsync($"/api/agents/{agent.Id}/versions", publishReq);
        Assert.Equal(HttpStatusCode.Created, publishResponse.StatusCode);

        var v2 = await publishResponse.Content.ReadFromJsonAsync<AgentVersion>(TestJson.Options);
        Assert.NotNull(v2);
        Assert.Equal(2, v2!.VersionNumber);
        Assert.False(string.IsNullOrWhiteSpace(v2.DigestSha256));
        Assert.Equal(64, v2.DigestSha256.Length); // SHA-256 hex digest

        // The digest must actually match the manifest content, not be a placeholder.
        var expectedDigest = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(v2Manifest))).ToLowerInvariant();
        Assert.Equal(expectedDigest, v2.DigestSha256);

        // Listing is newest-first.
        var listResponse = await client.GetAsync($"/api/agents/{agent.Id}/versions");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var versions = await listResponse.Content.ReadFromJsonAsync<List<AgentVersion>>(TestJson.Options);
        Assert.NotNull(versions);
        Assert.Equal(2, versions!.Count);
        Assert.Equal(2, versions[0].VersionNumber);
        Assert.Equal(1, versions[1].VersionNumber);

        // Publishing repoints the parent agent's current_version_id at the new version.
        var agentResponse = await client.GetAsync($"/api/agents/{agent.Id}");
        Assert.Equal(HttpStatusCode.OK, agentResponse.StatusCode);
        var refreshedAgent = await agentResponse.Content.ReadFromJsonAsync<Agent>(TestJson.Options);
        Assert.NotNull(refreshedAgent);
        Assert.Equal(v2.Id, refreshedAgent!.CurrentVersionId);
    }

    [Fact]
    public async Task GetVersion_ForUnknownAgent_Returns404()
    {
        var suffix = TestData.Suffix();
        var (client, _, _, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var versionsResponse = await client.GetAsync($"/api/agents/{agent.Id}/versions");
        var versions = await versionsResponse.Content.ReadFromJsonAsync<List<AgentVersion>>(TestJson.Options);
        var realVersionId = versions![0].Id;

        // Real version id, but under a made-up agent id -> the endpoint checks version.AgentId == agentId.
        var response = await client.GetAsync($"/api/agents/not-a-real-agent-id/versions/{realVersionId}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
