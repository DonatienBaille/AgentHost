using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHost.Api.Contracts;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Le contrat entre les deux moitiés de l'éditeur de manifeste.
///
/// Le mode formulaire du frontend émet du YAML canonique, et son test d'aller-retour
/// (<c>frontend/src/app/core/manifest/manifest-codec.spec.ts</c>) compare à un texte attendu, puis
/// rejoue la lecture avec une fabrique de test qui imite le parseur. Une fabrique qui imite peut
/// dériver du vrai parseur sans que personne ne s'en aperçoive — le test resterait vert pendant que
/// le produit casse.
///
/// Ce fichier rejoue **le même texte**, à l'octet près, contre le vrai
/// <c>IAgentManifestParser</c> et vérifie que le serveur en tire bien ce que le frontend suppose.
/// Si l'une des deux moitiés bouge, l'une des deux suites tombe.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class CanonicalManifestContractTests
{
    private readonly AgentHostApiFactory _factory;

    public CanonicalManifestContractTests(AgentHostApiFactory factory) => _factory = factory;

    /// <summary>
    /// Copie exacte de <c>CANONICAL_YAML</c> dans <c>manifest-codec.spec.ts</c> : ce que le mode
    /// formulaire produit pour le manifeste non trivial de <c>core/testing/fixtures.ts</c>.
    /// </summary>
    private const string CanonicalYaml = """
        apiVersion: agenthost.dev/v1
        kind: Agent
        metadata:
          name: redacteur
          displayName: Rédacteur de release notes
          description: Rédige les notes de version à partir des commits
        spec:
          type: claude_code
          image: ghcr.io/acme/redacteur:1.4.0
          external:
            provider: claude_code
            model: claude-opus-4
            config:
              temperature: "0.2"
              maxTurns: "8"
          inputs:
            type: object
            properties:
              repository:
                type: string
                title: Dépôt
                description: Dépôt à analyser
              sinceTag:
                type: string
                description: Tag de départ
                default: v1.0.0
              maxCommits:
                type: integer
                description: Nombre de commits max
                default: 200
              temperature:
                type: number
                default: 0.2
              draft:
                type: boolean
                description: Publier en brouillon
                default: true
              tone:
                type: string
                description: Ton
                default: neutre
                enum:
                - neutre
                - commercial
            required:
            - repository
            - sinceTag
          outputs:
            type: object
            properties:
              markdown:
                type: string
                description: Notes rédigées
            required:
            - markdown
          permissions:
            vcs: write_pr
            network: allowlist
            networkAllowlist:
            - api.github.com
            - registry.npmjs.org
            secrets:
            - GITHUB_TOKEN
            - ANTHROPIC_API_KEY
            docker: true
            writableRootfs: true
          runtime:
            profile: large
            cpu: 4
            memory: 8Gi
            disk: 20Gi
            maxDurationSeconds: 1800
          budget:
            defaultMaxUsd: 2.5
            hardMaxUsd: 7.5
          approvals:
            beforeWrite:
              requiredRole: maintainer
              requiredCount: 2
        """;

    private async Task<ValidateManifestResponse> ValidateAsync()
    {
        var bootstrap = _factory.CreateClient();
        var auth = await TestData.RegisterAsync(bootstrap);
        var client = TestData.AuthedClient(_factory, auth.Token);

        var response = await client.PostJsonAsync(
            "/api/agents/validate-manifest", new ValidateManifestRequest { ManifestYaml = CanonicalYaml });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ValidateManifestResponse>(TestJson.Options);
        Assert.NotNull(body);
        return body!;
    }

    [Fact]
    public async Task TheFormsOutput_ParsesIntoTheManifestTheFormAssumes()
    {
        var body = await ValidateAsync();

        Assert.True(body.Valid);
        var manifest = body.Manifest!;
        Assert.Equal("agenthost.dev/v1", manifest.ApiVersion);
        Assert.Equal("Agent", manifest.Kind);
        Assert.Equal("redacteur", manifest.Metadata.Name);
        Assert.Equal("Rédacteur de release notes", manifest.Metadata.DisplayName);
        Assert.Equal("Rédige les notes de version à partir des commits", manifest.Metadata.Description);

        Assert.Equal("claude_code", manifest.Spec.Type);
        Assert.Equal("ghcr.io/acme/redacteur:1.4.0", manifest.Spec.Image);

        Assert.NotNull(manifest.Spec.External);
        Assert.Equal("claude_code", manifest.Spec.External!.Provider);
        Assert.Equal("claude-opus-4", manifest.Spec.External.Model);
        Assert.Equal("0.2", manifest.Spec.External.Config["temperature"]);
        Assert.Equal("8", manifest.Spec.External.Config["maxTurns"]);

        Assert.Equal("write_pr", manifest.Spec.Permissions.Vcs);
        Assert.Equal("allowlist", manifest.Spec.Permissions.Network);
        Assert.Equal(new[] { "GITHUB_TOKEN", "ANTHROPIC_API_KEY" }, manifest.Spec.Permissions.Secrets);
        Assert.True(manifest.Spec.Permissions.Docker);

        Assert.Equal("large", manifest.Spec.Runtime.Profile);
        Assert.Equal(4, manifest.Spec.Runtime.Cpu);
        Assert.Equal("8Gi", manifest.Spec.Runtime.Memory);
        Assert.Equal("20Gi", manifest.Spec.Runtime.Disk);
        Assert.Equal(1800, manifest.Spec.Runtime.MaxDurationSeconds);

        Assert.Equal(2.5m, manifest.Spec.Budget.DefaultMaxUsd);
        Assert.Equal(7.5m, manifest.Spec.Budget.HardMaxUsd);

        Assert.NotNull(manifest.Spec.Approvals?.BeforeWrite);
        Assert.Equal("maintainer", manifest.Spec.Approvals!.BeforeWrite!.RequiredRole);
        Assert.Equal(2, manifest.Spec.Approvals.BeforeWrite.RequiredCount);

        Assert.Equal(
            new[] { "api.github.com", "registry.npmjs.org" },
            body.PermissionExtensions!.NetworkAllowlist);
        Assert.True(body.PermissionExtensions.WritableRootfs);
    }

    [Fact]
    public async Task TheFormsOutput_KeepsItsSchemaScalarTypesInTheRawDocument()
    {
        var body = await ValidateAsync();

        var properties = body.Document!["spec"]!["inputs"]!["properties"]!;
        Assert.Equal(JsonValueKind.Number, properties["maxCommits"]!["default"]!.GetValueKind());
        Assert.Equal(200, (int)properties["maxCommits"]!["default"]!);
        Assert.Equal(0.2, (double)properties["temperature"]!["default"]!, 6);
        Assert.Equal(JsonValueKind.True, properties["draft"]!["default"]!.GetValueKind());
        // "0.2" est cité dans external.config : il doit rester une chaîne, sinon le formulaire le
        // réécrirait non cité et changerait le manifeste sans le dire.
        Assert.Equal(
            JsonValueKind.String,
            body.Document!["spec"]!["external"]!["config"]!["temperature"]!.GetValueKind());

        var tone = properties["tone"]!;
        Assert.Equal("neutre", (string?)tone["default"]);
        Assert.Equal(new[] { "neutre", "commercial" }, tone["enum"]!.AsArray().Select(n => (string?)n));

        var required = body.Document!["spec"]!["inputs"]!["required"]!.AsArray();
        Assert.Equal(new[] { "repository", "sinceTag" }, required.Select(n => (string?)n));
    }

    [Fact]
    public async Task TheFormsOutput_HasNoKeyTheFormDoesNotModel()
    {
        var body = await ValidateAsync();

        // Le formulaire n'émet que ce qu'il sait relire : si cette liste s'allongeait sans que le
        // convertisseur suive, l'utilisateur verrait un bandeau « ceci ne sera pas éditable » sur
        // du YAML que le formulaire vient lui-même d'écrire.
        var root = body.Document!.AsObject().Select(kv => kv.Key).ToArray();
        Assert.Equal(new[] { "apiVersion", "kind", "metadata", "spec" }, root);

        var spec = body.Document!["spec"]!.AsObject().Select(kv => kv.Key).ToArray();
        Assert.Equal(
            new[] { "type", "image", "external", "inputs", "outputs", "permissions", "runtime", "budget", "approvals" },
            spec);
    }
}
