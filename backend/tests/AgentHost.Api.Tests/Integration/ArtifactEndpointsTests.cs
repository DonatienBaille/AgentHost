using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Exercises upload/list/get/download against a real run row. RunService.CreateAsync inserts the
/// run synchronously before kicking off container launch in a background scope, so the run id is
/// usable here even though the actual Docker launch will fail in this sandbox (no docker daemon)
/// and the run will eventually land in infra_error — that's fine, artifacts are keyed on run id
/// alone and don't care about the run's terminal status.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ArtifactEndpointsTests
{
    private readonly AgentHostApiFactory _factory;

    public ArtifactEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    [Fact]
    public async Task UploadListGetDownload_RoundTripsExactBytes()
    {
        var suffix = TestData.Suffix();
        var (client, _, _, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var runResponse = await client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = agent.Id });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = await runResponse.Content.ReadFromJsonAsync<Run>(TestJson.Options);
        Assert.NotNull(run);

        var fileBytes = System.Text.Encoding.UTF8.GetBytes($"integration test artifact payload {suffix} ☃\n");

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        form.Add(fileContent, "file", "report.txt");
        form.Add(new StringContent("report"), "artifactType");

        var uploadResponse = await client.PostAsync($"/api/runs/{run!.Id}/artifacts", form);
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);
        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<Artifact>(TestJson.Options);
        Assert.NotNull(uploaded);
        Assert.Equal(run.Id, uploaded!.RunId);
        Assert.Equal("report.txt", uploaded.Name);
        Assert.Equal("report", uploaded.ArtifactType);
        Assert.Equal(fileBytes.Length, uploaded.SizeBytes);

        // List for the run includes it.
        var listResponse = await client.GetAsync($"/api/runs/{run.Id}/artifacts");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<Artifact>>(TestJson.Options);
        Assert.NotNull(list);
        Assert.Contains(list!, a => a.Id == uploaded.Id);

        // Metadata fetch.
        var getResponse = await client.GetAsync($"/api/artifacts/{uploaded.Id}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
        var fetched = await getResponse.Content.ReadFromJsonAsync<Artifact>(TestJson.Options);
        Assert.Equal(uploaded.Id, fetched!.Id);

        // Download returns the exact bytes uploaded.
        var downloadResponse = await client.GetAsync($"/api/artifacts/{uploaded.Id}/download");
        Assert.Equal(HttpStatusCode.OK, downloadResponse.StatusCode);
        var downloadedBytes = await downloadResponse.Content.ReadAsByteArrayAsync();
        Assert.Equal(fileBytes, downloadedBytes);
    }

    /// <summary>
    /// The artifact name is concatenated into a filesystem path, so a traversal payload must be
    /// neutralised rather than merely happening to miss. The upload is accepted, but the stored
    /// name is reduced to a single safe path segment and the file lands inside the run directory.
    /// </summary>
    [Theory]
    [InlineData("../../../../etc/cron.d/pwned", "pwned")]
    [InlineData("..\\..\\windows\\system32\\evil.dll", "evil.dll")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("subdir/report.txt", "report.txt")]
    public async Task UploadArtifact_SanitizesTraversalInTheName(string maliciousName, string expectedStoredName)
    {
        var suffix = TestData.Suffix();
        var (client, _, _, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var runResponse = await client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = agent.Id });
        var run = await runResponse.Content.ReadFromJsonAsync<Run>(TestJson.Options);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("payload"u8.ToArray()), "file", "placeholder.bin");
        form.Add(new StringContent(maliciousName), "name");

        var uploadResponse = await client.PostAsync($"/api/runs/{run!.Id}/artifacts", form);
        Assert.Equal(HttpStatusCode.Created, uploadResponse.StatusCode);

        var uploaded = await uploadResponse.Content.ReadFromJsonAsync<Artifact>(TestJson.Options);
        Assert.NotNull(uploaded);

        // No separator or traversal token survives into the stored name...
        Assert.Equal(expectedStoredName, uploaded!.Name);
        Assert.DoesNotContain("..", uploaded.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("/", uploaded.Name, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", uploaded.Name, StringComparison.Ordinal);

        // ...and the file really was written inside this run's own directory.
        Assert.Contains($"/{run.Id}/", uploaded.S3Path, StringComparison.Ordinal);
        Assert.EndsWith($"{uploaded.Id}-{expectedStoredName}", uploaded.S3Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadArtifact_WithAnUnusableName_Returns400()
    {
        var suffix = TestData.Suffix();
        var (client, _, _, agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var runResponse = await client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = agent.Id });
        var run = await runResponse.Content.ReadFromJsonAsync<Run>(TestJson.Options);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("payload"u8.ToArray()), "file", "placeholder.bin");
        form.Add(new StringContent("../.."), "name");

        var uploadResponse = await client.PostAsync($"/api/runs/{run!.Id}/artifacts", form);
        Assert.Equal(HttpStatusCode.BadRequest, uploadResponse.StatusCode);
    }

    [Fact]
    public async Task GetArtifact_WithMissingId_Returns404()
    {
        var suffix = TestData.Suffix();
        var (client, _, _, _) = await TestData.CreateFullFixtureAsync(_factory, suffix);

        var response = await client.GetAsync($"/api/artifacts/not-a-real-artifact-id-{suffix}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
