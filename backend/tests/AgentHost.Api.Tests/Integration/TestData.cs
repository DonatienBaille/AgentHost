using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Shared request/response JSON options matching Program.cs's ConfigureHttpJsonOptions (Web
/// defaults i.e. camelCase + the same snake_case enum converters), so tests can deserialize real
/// domain types (User, Run, Agent, ...) exactly as the wire format produces them.
/// </summary>
public static class TestJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new RunStatusJsonConverter());
        options.Converters.Add(new AgentTypeJsonConverter());
        options.Converters.Add(new TriggeredByTypeJsonConverter());
        options.Converters.Add(new ApprovalTypeJsonConverter());
        options.Converters.Add(new ApprovalStatusJsonConverter());
        options.Converters.Add(new SecretScopeJsonConverter());
        options.Converters.Add(new UserRoleJsonConverter());
        return options;
    }
}

/// <summary>
/// HttpClient JSON-request helpers that serialize with <see cref="TestJson.Options"/> instead of
/// System.Text.Json's bare defaults. This matters: several request contracts (CreateRunRequest,
/// CreateUserRequest, CreateSecretRequest, ...) carry enum properties whose custom converters on
/// the server (RunStatusJsonConverter et al.) only accept the lowercase/snake_case string form
/// ("manual", "viewer") and throw on the default numeric encoding — plain PostAsJsonAsync would
/// silently 400 on every request touching one of those fields.
/// </summary>
public static class HttpClientJsonExtensions
{
    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string requestUri, T value) =>
        client.PostAsJsonAsync(requestUri, value, TestJson.Options);

    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string requestUri, T value) =>
        client.PutAsJsonAsync(requestUri, value, TestJson.Options);
}

/// <summary>
/// Helpers to stand up org/user/project/agent fixtures over real HTTP calls, with unique
/// ULID-suffixed names/slugs per call so concurrent test classes never collide against the
/// shared, never-reset Postgres schema.
/// </summary>
public static class TestData
{
    public static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    public const string DefaultPassword = "correct-horse-battery-staple";

    /// <summary>Registers a brand-new org + owner user, returning the owner's bearer token and the parsed response.</summary>
    public static async Task<AuthResponse> RegisterAsync(HttpClient client, string? suffix = null)
    {
        suffix ??= Suffix();
        var req = new RegisterRequest
        {
            OrgName = $"Test Org {suffix}",
            OrgSlug = $"test-org-{suffix}",
            Email = $"owner-{suffix}@example.com",
            Password = DefaultPassword,
            DisplayName = "Test Owner",
        };

        var response = await client.PostJsonAsync("/api/auth/register", req);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        return body ?? throw new InvalidOperationException("Register did not return a body");
    }

    /// <summary>Returns an <see cref="HttpClient"/> pre-authenticated as the given user's bearer token.</summary>
    public static HttpClient AuthedClient(AgentHostApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// As an owner, creates an additional user in the same org at the given role and logs in as
    /// them, returning their bearer token.
    /// </summary>
    public static async Task<(string Token, User User)> CreateUserWithRoleAsync(
        HttpClient ownerClient, UserRole role, string? suffix = null)
    {
        suffix ??= Suffix();
        var email = $"{role.ToString().ToLowerInvariant()}-{suffix}@example.com";
        // No OrgId in the contract any more: the new user lands in the *caller's* org, taken
        // from the owner client's JWT.
        var createReq = new CreateUserRequest
        {
            Email = email,
            Password = DefaultPassword,
            DisplayName = $"Test {role}",
            Role = role,
        };

        var createResponse = await ownerClient.PostJsonAsync("/api/users", createReq);
        createResponse.EnsureSuccessStatusCode();

        var loginResponse = await ownerClient.PostJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = DefaultPassword });
        loginResponse.EnsureSuccessStatusCode();
        var auth = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        return (auth!.Token, auth.User);
    }

    public static async Task<Project> CreateProjectAsync(HttpClient client, string? suffix = null)
    {
        suffix ??= Suffix();
        var req = new CreateProjectRequest
        {
            Name = $"Test Project {suffix}",
            Slug = $"test-project-{suffix}",
            Description = "Integration test project",
            BudgetMonthlyUsd = 1000m,
        };

        var response = await client.PostJsonAsync("/api/projects", req);
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<Project>(TestJson.Options);
        return project ?? throw new InvalidOperationException("CreateProject did not return a body");
    }

    /// <summary>Minimal valid agent manifest YAML per AgentManifestParser: metadata.name + spec are required, everything else defaults.</summary>
    public static string BuildManifestYaml(string name) => $"""
        apiVersion: agenthost.dev/v1
        kind: Agent
        metadata:
          name: {name}
          displayName: {name}
          description: Integration test agent
        spec:
          type: oci
          image: busybox:latest
          runtime:
            profile: standard
            cpu: 1
            memory: 512Mi
            disk: 1Gi
            maxDurationSeconds: 300
          budget:
            defaultMaxUsd: 5
            hardMaxUsd: 10
        """;

    public static async Task<Agent> CreateAgentAsync(HttpClient client, string projectId, string? suffix = null)
    {
        suffix ??= Suffix();
        var req = new CreateAgentRequest
        {
            ProjectId = projectId,
            Name = $"Test Agent {suffix}",
            Slug = $"test-agent-{suffix}",
            ManifestYaml = BuildManifestYaml($"test-agent-{suffix}"),
            Publish = true,
        };

        var response = await client.PostJsonAsync("/api/agents", req);
        response.EnsureSuccessStatusCode();
        var agent = await response.Content.ReadFromJsonAsync<Agent>(TestJson.Options);
        return agent ?? throw new InvalidOperationException("CreateAgent did not return a body");
    }

    /// <summary>Full org + owner-client + project + published agent fixture, the common setup for run/artifact/webhook tests.</summary>
    public static async Task<(HttpClient Client, AuthResponse Auth, Project Project, Agent Agent)> CreateFullFixtureAsync(
        AgentHostApiFactory factory, string? suffix = null)
    {
        suffix ??= Suffix();
        var bootstrapClient = factory.CreateClient();
        var auth = await RegisterAsync(bootstrapClient, suffix);
        var client = AuthedClient(factory, auth.Token);
        var project = await CreateProjectAsync(client, suffix);
        var agent = await CreateAgentAsync(client, project.Id, suffix);
        return (client, auth, project, agent);
    }
}
