using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// POST /api/agents/validate-manifest — the dry-run parse the manifest editor calls while the user
/// types. It must never create anything, must reuse the real parser, and must locate failures.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class ManifestValidationEndpointTests
{
    private readonly AgentHostApiFactory _factory;

    public ManifestValidationEndpointTests(AgentHostApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AuthedAsync()
    {
        var bootstrap = _factory.CreateClient();
        var auth = await TestData.RegisterAsync(bootstrap);
        return TestData.AuthedClient(_factory, auth.Token);
    }

    private static async Task<ValidateManifestResponse> ValidateAsync(HttpClient client, string yaml)
    {
        var response = await client.PostJsonAsync(
            "/api/agents/validate-manifest", new ValidateManifestRequest { ManifestYaml = yaml });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidateManifestResponse>(TestJson.Options);
        Assert.NotNull(body);
        return body!;
    }

    [Fact]
    public async Task RequiresAuthentication()
    {
        var anonymous = _factory.CreateClient();

        var response = await anonymous.PostJsonAsync(
            "/api/agents/validate-manifest", new ValidateManifestRequest { ManifestYaml = "x" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidManifest_ReturnsNormalizedManifestWithParserDefaults()
    {
        var client = await AuthedAsync();

        // Only the two fields the parser requires: everything else must come back defaulted.
        var body = await ValidateAsync(client, """
            metadata:
              name: minimal-agent
            spec:
              type: oci
            """);

        Assert.True(body.Valid);
        Assert.Null(body.Error);
        Assert.NotNull(body.Manifest);
        Assert.Equal("minimal-agent", body.Manifest!.Metadata.Name);
        // displayName falls back to name, per the parser.
        Assert.Equal("minimal-agent", body.Manifest.Metadata.DisplayName);
        Assert.Equal("agenthost.dev/v1", body.Manifest.ApiVersion);
        Assert.Equal("standard", body.Manifest.Spec.Runtime.Profile);
        Assert.Equal(3600, body.Manifest.Spec.Runtime.MaxDurationSeconds);
        Assert.Equal(5.0m, body.Manifest.Spec.Budget.DefaultMaxUsd);
        Assert.Equal("none", body.Manifest.Spec.Permissions.Network);
    }

    [Fact]
    public async Task ValidManifest_SurfacesPermissionExtensionsTheTypedManifestDoesNotCarry()
    {
        var client = await AuthedAsync();

        var body = await ValidateAsync(client, """
            metadata:
              name: netted-agent
            spec:
              type: oci
              permissions:
                network: allowlist
                networkAllowlist:
                  - api.github.com
                  - registry.npmjs.org
                writableRootfs: true
            """);

        Assert.True(body.Valid);
        Assert.NotNull(body.PermissionExtensions);
        Assert.Equal(
            new[] { "api.github.com", "registry.npmjs.org" },
            body.PermissionExtensions!.NetworkAllowlist);
        Assert.True(body.PermissionExtensions.WritableRootfs);
    }

    [Fact]
    public async Task ValidManifest_RawDocumentKeepsUnknownKeysAndScalarTypes()
    {
        var client = await AuthedAsync();

        var body = await ValidateAsync(client, """
            metadata:
              name: raw-agent
            spec:
              type: oci
              somethingTheParserIgnores: keep-me
              inputs:
                type: object
                properties:
                  retries:
                    type: integer
                    default: 3
                  verbose:
                    type: boolean
                    default: true
                  label:
                    type: string
                    default: "7"
            """);

        Assert.True(body.Valid);
        Assert.NotNull(body.Document);
        var spec = body.Document!["spec"]!.AsObject();

        // The typed parser drops unknown keys; the raw projection must not.
        Assert.Equal("keep-me", (string?)spec["somethingTheParserIgnores"]);

        var properties = spec["inputs"]!["properties"]!;
        // Scalars keep their YAML type: an int stays an int, a bool stays a bool...
        Assert.Equal(JsonValueKind.Number, properties["retries"]!["default"]!.GetValueKind());
        Assert.Equal(3, (int)properties["retries"]!["default"]!);
        Assert.Equal(JsonValueKind.True, properties["verbose"]!["default"]!.GetValueKind());
        // ...and a quoted digit stays a string, which is the distinction the typed parser loses.
        Assert.Equal(JsonValueKind.String, properties["label"]!["default"]!.GetValueKind());

        // Contrast, on the very same response: the typed manifest stringifies everything.
        var typedDefault = body.Manifest!.Spec.Inputs["properties"];
        Assert.NotNull(typedDefault);
    }

    [Theory]
    // Bad indentation inside metadata: YamlDotNet reports the offending line.
    [InlineData("""
        metadata:
          name: x
           oops: bad-indent
        spec:
          type: oci
        """, 3)]
    // A type conversion failure deeper in the tree: the line is the scalar's, not the document's.
    [InlineData("""
        metadata:
          name: x
        spec:
          type: oci
          runtime:
            cpu: not-a-number
        """, 6)]
    public async Task MalformedManifest_ReportsTheFailingLine(string yaml, int expectedLine)
    {
        var client = await AuthedAsync();

        var body = await ValidateAsync(client, yaml);

        Assert.False(body.Valid);
        Assert.Null(body.Manifest);
        Assert.NotNull(body.Error);
        Assert.Equal(expectedLine, body.Error!.Line);
        Assert.False(string.IsNullOrWhiteSpace(body.Error.Message));
    }

    [Fact]
    public async Task StructuralError_WithoutAPosition_ReportsNoLineRatherThanAWrongOne()
    {
        var client = await AuthedAsync();

        // Parses as YAML, rejected by the parser's own rules — YamlDotNet never sees a problem, so
        // there is no position to report.
        var body = await ValidateAsync(client, """
            metadata:
              displayName: nameless
            spec:
              type: oci
            """);

        Assert.False(body.Valid);
        Assert.Null(body.Error!.Line);
        Assert.Contains("metadata.name", body.Error.Message);
    }

    [Fact]
    public async Task EmptyManifest_IsInvalidAndDoesNotThrow()
    {
        var client = await AuthedAsync();

        var body = await ValidateAsync(client, "   ");

        Assert.False(body.Valid);
        Assert.NotNull(body.Error);
        Assert.Null(body.Error!.Line);
    }

    [Fact]
    public async Task Validating_CreatesNoAgent()
    {
        var client = await AuthedAsync();
        var project = await TestData.CreateProjectAsync(client);

        await ValidateAsync(client, TestData.BuildManifestYaml("ghost-agent"));

        var listResponse = await client.GetAsync($"/api/agents?projectId={project.Id}");
        var agents = await listResponse.Content.ReadFromJsonAsync<List<Agent>>(TestJson.Options);
        Assert.Empty(agents!);
    }

    [Fact]
    public async Task AViewer_MayValidate_ItIsAReadOnlyDryRun()
    {
        var bootstrap = _factory.CreateClient();
        var auth = await TestData.RegisterAsync(bootstrap);
        var ownerClient = TestData.AuthedClient(_factory, auth.Token);
        var (viewerToken, _) = await TestData.CreateUserWithRoleAsync(ownerClient, UserRole.Viewer);
        var viewerClient = TestData.AuthedClient(_factory, viewerToken);

        var body = await ValidateAsync(viewerClient, TestData.BuildManifestYaml("viewer-check"));

        Assert.True(body.Valid);
    }
}
