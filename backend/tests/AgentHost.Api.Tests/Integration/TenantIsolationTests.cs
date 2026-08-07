using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// The core security guarantee of a multi-tenant system: an authenticated user of organization A
/// can neither read nor mutate anything owned by organization B, and list endpoints never spill
/// rows across the boundary.
///
/// Two independent orgs are registered over real HTTP, org B builds a full fixture (project,
/// agent, run, artifact, secret, webhook, project memory), and org A — a legitimate, fully
/// authenticated *owner* of its own org, i.e. the highest-privilege role there is — then aims
/// every route at org B's ULIDs.
///
/// Every cross-tenant attempt must answer **404, not 403**: a 403 confirms the id exists, which
/// turns any of these routes into an oracle for enumerating another tenant's resources. That
/// distinction is asserted explicitly throughout.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class TenantIsolationTests
{
    private readonly AgentHostApiFactory _factory;

    public TenantIsolationTests(AgentHostApiFactory factory) => _factory = factory;

    /// <summary>Two fully-separate tenants, each with an owner client and a complete resource set.</summary>
    private sealed record Tenant(
        HttpClient Client,
        AuthResponse Auth,
        Project Project,
        Agent Agent)
    {
        public string OrgId => Auth.User.OrgId;
    }

    private async Task<Tenant> CreateTenantAsync()
    {
        var (client, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory);
        return new Tenant(client, auth, project, agent);
    }

    // ---- Reads -------------------------------------------------------------------------------

    [Fact]
    public async Task OrgA_CannotReadOrgBsRunProjectAgentOrOrganization()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var bRun = await CreateRunAsync(b);

        await AssertNotFound(a.Client.GetAsync($"/api/runs/{bRun.Id}"));
        await AssertNotFound(a.Client.GetAsync($"/api/runs/{bRun.Id}/events"));
        await AssertNotFound(a.Client.GetAsync($"/api/runs/{bRun.Id}/logs"));
        await AssertNotFound(a.Client.GetAsync($"/api/projects/{b.Project.Id}"));
        await AssertNotFound(a.Client.GetAsync($"/api/agents/{b.Agent.Id}"));
        await AssertNotFound(a.Client.GetAsync($"/api/organizations/{b.OrgId}"));
        await AssertNotFound(a.Client.GetAsync($"/api/users/{b.Auth.User.Id}"));
    }

    [Fact]
    public async Task OrgA_CannotReadOrgBsAuditLog()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        // Generate an audit entry in B so there is genuinely something to leak.
        var bRun = await CreateRunAsync(b);
        await CreateRunAsync(a);

        await AssertNotFound(a.Client.GetAsync($"/api/organizations/{b.OrgId}/audit-log"));
        await AssertNotFound(a.Client.GetAsync($"/api/organizations/{b.OrgId}/audit-log/facets"));

        // A's own audit log is readable, and B's activity is nowhere in it. The served entries no
        // longer carry an org id to compare — the scope comes from the JWT, so the server has
        // nothing to echo back — hence the check is on B's actual run id, which is the thing a
        // leak would expose.
        var ownResponse = await a.Client.GetAsync($"/api/organizations/{a.OrgId}/audit-log");
        Assert.Equal(HttpStatusCode.OK, ownResponse.StatusCode);
        var page = await ownResponse.Content.ReadFromJsonAsync<AuditPage>(TestJson.Options);
        Assert.NotNull(page);
        Assert.NotEmpty(page!.Items);
        Assert.DoesNotContain(page.Items, e => e.ResourceId == bRun.Id);
        // The total is part of the answer too: counting another tenant's entries leaks their
        // volume of activity without showing a single line.
        Assert.Equal(page.Items.Count, page.Total);
    }

    [Fact]
    public async Task OrgA_CannotReadOrgBsArtifactOrDownloadIt()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var bRun = await CreateRunAsync(b);
        var bArtifact = await UploadArtifactAsync(b, bRun.Id, "confidential.txt", "org B private bytes");

        await AssertNotFound(a.Client.GetAsync($"/api/artifacts/{bArtifact.Id}"));
        await AssertNotFound(a.Client.GetAsync($"/api/artifacts/{bArtifact.Id}/download"));

        // Listing B's run artifacts through A must not reveal them either. (The run itself is
        // invisible to A, so an empty list is the correct answer here.)
        var listResponse = await a.Client.GetAsync($"/api/runs/{bRun.Id}/artifacts");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<Artifact>>(TestJson.Options);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task OrgA_CannotReadOrgBsSecretMetadata()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var bSecretId = await CreateSecretAsync(b, "org-b-api-key", "org-b-plaintext");

        await AssertNotFound(a.Client.GetAsync($"/api/secrets/{bSecretId}"));

        var listResponse = await a.Client.GetAsync("/api/secrets");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<SecretResponse>>(TestJson.Options);
        Assert.NotNull(list);
        Assert.DoesNotContain(list!, s => s.Id == bSecretId);
        Assert.All(list!, s => Assert.Equal(a.OrgId, s.OrgId));
    }

    [Fact]
    public async Task OrgA_CannotReadOrgBsWebhookOrProjectMemory()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var bWebhook = await CreateWebhookAsync(b);

        // A webhook leak hands over the HMAC signing secret, so this one matters twice over.
        await AssertNotFound(a.Client.GetAsync($"/api/webhooks/{bWebhook.Id}"));
        await AssertNotFound(a.Client.GetAsync($"/api/projects/{b.Project.Id}/memory"));

        var listResponse = await a.Client.GetAsync($"/api/webhooks?projectId={b.Project.Id}");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<Webhook>>(TestJson.Options);
        Assert.Empty(list!);
    }

    [Fact]
    public async Task OrgA_CannotReadOrgBsAgentVersions()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var bVersionsResponse = await b.Client.GetAsync($"/api/agents/{b.Agent.Id}/versions");
        var bVersions = await bVersionsResponse.Content.ReadFromJsonAsync<List<AgentVersion>>(TestJson.Options);
        var bVersionId = bVersions![0].Id;

        await AssertNotFound(a.Client.GetAsync($"/api/agents/{b.Agent.Id}/versions/{bVersionId}"));

        var listResponse = await a.Client.GetAsync($"/api/agents/{b.Agent.Id}/versions");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await listResponse.Content.ReadFromJsonAsync<List<AgentVersion>>(TestJson.Options);
        Assert.Empty(list!);
    }

    // ---- Writes ------------------------------------------------------------------------------

    [Fact]
    public async Task OrgA_CannotMutateOrgBsProjectAgentOrOrganization()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        await AssertNotFound(a.Client.PutJsonAsync($"/api/projects/{b.Project.Id}",
            new UpdateProjectRequest { Name = "pwned" }));
        await AssertNotFound(a.Client.PutJsonAsync($"/api/agents/{b.Agent.Id}",
            new UpdateAgentRequest { Name = "pwned" }));
        await AssertNotFound(a.Client.DeleteAsync($"/api/agents/{b.Agent.Id}"));
        await AssertNotFound(a.Client.DeleteAsync($"/api/projects/{b.Project.Id}"));
        await AssertNotFound(a.Client.PutJsonAsync($"/api/organizations/{b.OrgId}",
            new UpdateOrganizationRequest { Name = "pwned" }));
        await AssertNotFound(a.Client.DeleteAsync($"/api/organizations/{b.OrgId}"));

        // None of it landed: B still sees its own untouched resources.
        var projectResponse = await b.Client.GetAsync($"/api/projects/{b.Project.Id}");
        Assert.Equal(HttpStatusCode.OK, projectResponse.StatusCode);
        var project = await projectResponse.Content.ReadFromJsonAsync<Project>(TestJson.Options);
        Assert.NotEqual("pwned", project!.Name);

        var agentResponse = await b.Client.GetAsync($"/api/agents/{b.Agent.Id}");
        Assert.Equal(HttpStatusCode.OK, agentResponse.StatusCode);
        var agent = await agentResponse.Content.ReadFromJsonAsync<Agent>(TestJson.Options);
        Assert.NotEqual("pwned", agent!.Name);
    }

    [Fact]
    public async Task OrgA_CannotStartCancelOrApproveRunsOnOrgBsResources()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var bRun = await CreateRunAsync(b);

        // Starting a run against B's agent would bill B and expose B's outputs to A.
        await AssertNotFound(a.Client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = b.Agent.Id }));

        await AssertNotFound(a.Client.PostJsonAsync($"/api/runs/{bRun.Id}/cancel", new { }));
        await AssertNotFound(a.Client.PostJsonAsync($"/api/runs/{bRun.Id}/approve",
            new ApprovalRequest { Decision = "approve" }));
        await AssertNotFound(a.Client.PostJsonAsync($"/api/runs/{bRun.Id}/answer",
            new AnswerQuestionRequest { QuestionId = "q1", Answer = "yes" }));
    }

    [Fact]
    public async Task OrgA_CannotWriteIntoOrgBsSecretsWebhooksOrArtifacts()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var bRun = await CreateRunAsync(b);
        var bSecretId = await CreateSecretAsync(b, "org-b-rotate-target", "org-b-plaintext");
        var bWebhook = await CreateWebhookAsync(b);

        await AssertNotFound(a.Client.PutJsonAsync($"/api/secrets/{bSecretId}",
            new UpdateSecretRequest { Value = "attacker-controlled" }));
        await AssertNotFound(a.Client.DeleteAsync($"/api/secrets/{bSecretId}"));

        await AssertNotFound(a.Client.PutJsonAsync($"/api/webhooks/{bWebhook.Id}",
            new UpdateWebhookRequest { Url = "https://attacker.invalid/exfil" }));
        await AssertNotFound(a.Client.DeleteAsync($"/api/webhooks/{bWebhook.Id}"));

        // Creating a webhook on B's project would redirect B's run events to a host A controls.
        await AssertNotFound(a.Client.PostJsonAsync("/api/webhooks", new CreateWebhookRequest
        {
            ProjectId = b.Project.Id,
            Url = "https://attacker.invalid/exfil",
            Events = new List<string> { "run.created" },
        }));

        // Uploading into B's run directory.
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("planted"u8.ToArray()), "file", "planted.txt");
        await AssertNotFound(a.Client.PostAsync($"/api/runs/{bRun.Id}/artifacts", form));

        // And B's webhook is untouched, secret token included.
        var webhookResponse = await b.Client.GetAsync($"/api/webhooks/{bWebhook.Id}");
        Assert.Equal(HttpStatusCode.OK, webhookResponse.StatusCode);
        var webhook = await webhookResponse.Content.ReadFromJsonAsync<Webhook>(TestJson.Options);
        Assert.NotEqual("https://attacker.invalid/exfil", webhook!.Url);
    }

    [Fact]
    public async Task OrgA_CannotWriteOrgBsProjectMemoryOrCreateAgentsInIt()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        await AssertNotFound(a.Client.PostJsonAsync($"/api/projects/{b.Project.Id}/memory", new MemoryUpdate
        {
            NewNotes = new List<Note> { new() { Id = "n1", AgentName = "attacker", Text = "injected", Timestamp = DateTime.UtcNow } },
        }));
        await AssertNotFound(a.Client.PostJsonAsync($"/api/projects/{b.Project.Id}/memory/archive", new { }));

        // Filing an agent under B's project would let A's manifest run inside B's tenancy.
        await AssertNotFound(a.Client.PostJsonAsync("/api/agents", new CreateAgentRequest
        {
            ProjectId = b.Project.Id,
            Name = "intruder",
            Slug = $"intruder-{TestData.Suffix()}",
            ManifestYaml = TestData.BuildManifestYaml("intruder"),
        }));

        // Publishing a new version of B's agent.
        await AssertNotFound(a.Client.PostJsonAsync($"/api/agents/{b.Agent.Id}/versions",
            new PublishAgentVersionRequest { ManifestYaml = TestData.BuildManifestYaml("intruder-v2") }));
    }

    /// <summary>
    /// CreateUserRequest no longer carries an OrgId, so the old "create an owner inside the victim
    /// org, then log in as them" escalation is structurally impossible: the new user always lands
    /// in the caller's own org, whatever the request says.
    /// </summary>
    [Fact]
    public async Task CreatingAUser_AlwaysLandsInTheCallersOwnOrg()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var suffix = TestData.Suffix();
        var response = await a.Client.PostJsonAsync("/api/users", new CreateUserRequest
        {
            Email = $"escalation-{suffix}@example.com",
            Password = TestData.DefaultPassword,
            Role = UserRole.Owner,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var created = await response.Content.ReadFromJsonAsync<User>(TestJson.Options);
        Assert.Equal(a.OrgId, created!.OrgId);
        Assert.NotEqual(b.OrgId, created.OrgId);
    }

    // ---- Lists -------------------------------------------------------------------------------

    [Fact]
    public async Task ListEndpoints_ReturnOnlyTheCallersOwnOrgRows()
    {
        var a = await CreateTenantAsync();
        var b = await CreateTenantAsync();

        var aRun = await CreateRunAsync(a);
        var bRun = await CreateRunAsync(b);

        // Runs.
        var runs = await GetListAsync<Run>(a.Client, "/api/runs?take=200");
        Assert.Contains(runs, r => r.Id == aRun.Id);
        Assert.DoesNotContain(runs, r => r.Id == bRun.Id);
        Assert.All(runs, r => Assert.Equal(a.OrgId, r.OrgId));

        // Projects.
        var projects = await GetListAsync<Project>(a.Client, "/api/projects");
        Assert.Contains(projects, p => p.Id == a.Project.Id);
        Assert.DoesNotContain(projects, p => p.Id == b.Project.Id);
        Assert.All(projects, p => Assert.Equal(a.OrgId, p.OrgId));

        // Agents.
        var agents = await GetListAsync<Agent>(a.Client, "/api/agents");
        Assert.Contains(agents, x => x.Id == a.Agent.Id);
        Assert.DoesNotContain(agents, x => x.Id == b.Agent.Id);
        Assert.All(agents, x => Assert.Equal(a.OrgId, x.OrgId));

        // Users.
        var users = await GetListAsync<User>(a.Client, "/api/users");
        Assert.Contains(users, u => u.Id == a.Auth.User.Id);
        Assert.DoesNotContain(users, u => u.Id == b.Auth.User.Id);
        Assert.All(users, u => Assert.Equal(a.OrgId, u.OrgId));

        // Organizations: exactly one — the caller's own.
        var orgs = await GetListAsync<Organization>(a.Client, "/api/organizations");
        Assert.Equal(a.OrgId, Assert.Single(orgs).Id);

        // Narrowing by another tenant's projectId must not widen the scope either.
        var crossRuns = await GetListAsync<Run>(a.Client, $"/api/runs?projectId={b.Project.Id}");
        Assert.Empty(crossRuns);
        var crossAgents = await GetListAsync<Agent>(a.Client, $"/api/agents?projectId={b.Project.Id}");
        Assert.Empty(crossAgents);
    }

    /// <summary>
    /// Page size is clamped, so <c>?take=100000000</c> cannot be used to pull the whole table into
    /// memory (and <c>skip=-1</c> cannot produce a negative OFFSET, which Postgres rejects).
    /// </summary>
    [Fact]
    public async Task ListEndpoints_ClampPaginationParameters()
    {
        var a = await CreateTenantAsync();
        await CreateRunAsync(a);

        var runsResponse = await a.Client.GetAsync("/api/runs?take=100000000&skip=-5");
        Assert.Equal(HttpStatusCode.OK, runsResponse.StatusCode);
        var runs = await runsResponse.Content.ReadFromJsonAsync<List<Run>>(TestJson.Options);
        Assert.NotNull(runs);
        Assert.InRange(runs!.Count, 0, 200);

        var auditResponse = await a.Client.GetAsync($"/api/organizations/{a.OrgId}/audit-log?take=100000000&skip=-5");
        Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
        var page = await auditResponse.Content.ReadFromJsonAsync<AuditPage>(TestJson.Options);
        Assert.NotNull(page);
        Assert.InRange(page!.Items.Count, 0, 200);
        // The envelope reports the paging actually applied, so the clamp is observable and not
        // merely inferred from a short page.
        Assert.Equal(Paging.MaxTake, page.Take);
        Assert.Equal(0, page.Skip);
    }

    // ---- Helpers -----------------------------------------------------------------------------

    /// <summary>
    /// Asserts a cross-tenant attempt answered 404. Explicitly rejects 403 as well as success:
    /// "forbidden" would confirm the resource exists, which is exactly the leak being prevented.
    /// </summary>
    private static async Task AssertNotFound(Task<HttpResponseMessage> call)
    {
        var response = await call;
        Assert.False(response.StatusCode == HttpStatusCode.Forbidden,
            "Cross-tenant access must answer 404, not 403 — a 403 confirms the resource exists and lets ULIDs be probed.");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<List<T>> GetListAsync<T>(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var list = await response.Content.ReadFromJsonAsync<List<T>>(TestJson.Options);
        Assert.NotNull(list);
        return list!;
    }

    private static async Task<Run> CreateRunAsync(Tenant tenant)
    {
        var response = await tenant.Client.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = tenant.Agent.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<Run>(TestJson.Options);
        return run!;
    }

    private static async Task<Artifact> UploadArtifactAsync(Tenant tenant, string runId, string fileName, string content)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(content)), "file", fileName);
        var response = await tenant.Client.PostAsync($"/api/runs/{runId}/artifacts", form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var artifact = await response.Content.ReadFromJsonAsync<Artifact>(TestJson.Options);
        return artifact!;
    }

    private static async Task<string> CreateSecretAsync(Tenant tenant, string namePrefix, string value)
    {
        var response = await tenant.Client.PostJsonAsync("/api/secrets", new CreateSecretRequest
        {
            Name = $"{namePrefix}-{TestData.Suffix()}",
            Value = value,
            Scope = SecretScope.Org,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var secret = await response.Content.ReadFromJsonAsync<SecretResponse>(TestJson.Options);
        return secret!.Id;
    }

    private static async Task<Webhook> CreateWebhookAsync(Tenant tenant)
    {
        var response = await tenant.Client.PostJsonAsync("/api/webhooks", new CreateWebhookRequest
        {
            ProjectId = tenant.Project.Id,
            Url = "https://example.invalid/tenant-sink",
            Events = new List<string> { "run.created" },
            SecretToken = "tenant-webhook-secret",
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var webhook = await response.Content.ReadFromJsonAsync<Webhook>(TestJson.Options);
        return webhook!;
    }
}
