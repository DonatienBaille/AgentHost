using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Guards the *persisted* encoding of every enum-typed column.
///
/// Every one of these columns used to hold the CLR enum's ordinal rendered as text ('14', '0'),
/// because Dapper converts an enum parameter to its underlying integral type before it consults a
/// registered TypeHandler. The ordinal round-tripped through Enum.Parse on the way back, so the API
/// kept returning plausible values and nothing failed — while every SQL predicate written against
/// the documented values matched nothing. The visible casualty was
/// <see cref="ISecretRepository.ListForScopeAsync"/>, whose `scope = 'org'` filter matched zero
/// rows, meaning no run ever received a secret.
///
/// The three groups below are the assertions whose absence let that ship: a secret actually
/// reaching a run, the on-disk format of the columns, and a full round-trip of every enum member
/// (including the multi-word ones like 'awaiting_approval', which Enum.Parse cannot produce and
/// which therefore catch any regression to Dapper's built-in enum mapping).
/// </summary>
[Collection(IntegrationCollection.Name)]
public class EnumStorageEncodingTests
{
    private readonly AgentHostApiFactory _factory;

    public EnumStorageEncodingTests(AgentHostApiFactory factory) => _factory = factory;

    // ---------------------------------------------------------------- the live-bug regression

    /// <summary>
    /// The regression that would have caught the shipped bug: a secret declared in an agent's
    /// manifest must actually reach the run, decrypted, via <see cref="ISecretsBroker"/> — which
    /// only works if <c>secrets.scope</c> holds 'org'/'project' rather than an ordinal.
    /// Also pins the scoping rules: a secret the manifest does not declare, and a project-scoped
    /// secret belonging to a different project, must not leak into the run.
    /// </summary>
    [Fact]
    public async Task Manifest_declared_secrets_reach_the_run_decrypted()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var auth = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, auth.Token);

        var project = await TestData.CreateProjectAsync(client, suffix);
        var otherProject = await TestData.CreateProjectAsync(client, $"{suffix}b");

        var orgSecretName = $"ORG_TOKEN_{suffix}";
        var projectSecretName = $"PROJECT_TOKEN_{suffix}";
        var undeclaredSecretName = $"UNDECLARED_TOKEN_{suffix}";
        var otherProjectSecretName = $"OTHER_PROJECT_TOKEN_{suffix}";

        await CreateSecretAsync(client, orgSecretName, "org-plaintext-value", SecretScope.Org, projectId: null);
        await CreateSecretAsync(client, projectSecretName, "project-plaintext-value", SecretScope.Project, project.Id);
        await CreateSecretAsync(client, undeclaredSecretName, "must-not-leak", SecretScope.Org, projectId: null);
        await CreateSecretAsync(client, otherProjectSecretName, "must-not-leak", SecretScope.Project, otherProject.Id);

        // The agent's manifest declares exactly the two secrets the run is entitled to.
        var manifestYaml = ManifestWithSecrets($"secret-agent-{suffix}", orgSecretName, projectSecretName, otherProjectSecretName);
        var agent = await CreateAgentAsync(client, project.Id, suffix, manifestYaml);

        using var scope = _factory.Services.CreateScope();
        var parser = scope.ServiceProvider.GetRequiredService<IAgentManifestParser>();
        var broker = scope.ServiceProvider.GetRequiredService<ISecretsBroker>();

        // Exactly what RunService does before launching a container.
        var declaredNames = parser.Parse(agent.ManifestYaml).Spec.Permissions.Secrets;
        Assert.Contains(orgSecretName, declaredNames);

        var runId = UlidGenerator.NewUlid();
        var resolved = await broker.ResolveForRunAsync(auth.User.OrgId, project.Id, runId, declaredNames);

        Assert.Equal("org-plaintext-value", Assert.Contains(orgSecretName, resolved));
        Assert.Equal("project-plaintext-value", Assert.Contains(projectSecretName, resolved));
        Assert.DoesNotContain(undeclaredSecretName, resolved.Keys);
        Assert.DoesNotContain(otherProjectSecretName, resolved.Keys); // project-scoped, wrong project
    }

    // ---------------------------------------------------------------- storage format

    /// <summary>
    /// Rows created through the public API must land in the database as lowercase/snake_case
    /// strings — never as the digits the enum ordinal would produce.
    /// </summary>
    [Fact]
    public async Task Rows_created_through_the_api_store_snake_case_strings()
    {
        var suffix = TestData.Suffix();
        var bootstrap = _factory.CreateClient();
        var auth = await TestData.RegisterAsync(bootstrap, suffix);
        var client = TestData.AuthedClient(_factory, auth.Token);
        var project = await TestData.CreateProjectAsync(client, suffix);
        var agent = await TestData.CreateAgentAsync(client, project.Id, suffix);

        // users.role — the registering owner, plus an explicitly created viewer.
        var (_, viewer) = await TestData.CreateUserWithRoleAsync(client, UserRole.Viewer, suffix);
        Assert.Equal("owner", await ScalarAsync("SELECT role FROM users WHERE id = @Id", new { Id = auth.User.Id }));
        Assert.Equal("viewer", await ScalarAsync("SELECT role FROM users WHERE id = @Id", new { Id = viewer.Id }));

        // secrets.scope
        var orgSecretId = await CreateSecretAsync(client, $"FMT_ORG_{suffix}", "v", SecretScope.Org, null);
        var projectSecretId = await CreateSecretAsync(client, $"FMT_PROJ_{suffix}", "v", SecretScope.Project, project.Id);
        Assert.Equal("org", await ScalarAsync("SELECT scope FROM secrets WHERE id = @Id", new { Id = orgSecretId }));
        Assert.Equal("project", await ScalarAsync("SELECT scope FROM secrets WHERE id = @Id", new { Id = projectSecretId }));

        // agents.agent_type
        Assert.Equal("oci", await ScalarAsync("SELECT agent_type FROM agents WHERE id = @Id", new { Id = agent.Id }));

        // runs.status / runs.triggered_by_type. The run executes in the background (and fails,
        // there being no Docker daemon under test), so the *value* of status is a moving target —
        // but whatever it is, it must be a legal snake_case status and never a digit.
        var runResponse = await client.PostJsonAsync("/api/runs", new CreateRunRequest
        {
            AgentId = agent.Id,
            Inputs = new JsonObject(),
        });
        runResponse.EnsureSuccessStatusCode();
        var run = await runResponse.Content.ReadFromJsonAsync<Run>(TestJson.Options);

        var storedStatus = await ScalarAsync("SELECT status FROM runs WHERE id = @Id", new { Id = run!.Id });
        Assert.Matches("^[a-z_]+$", storedStatus!);
        RunStatusExtensions.FromDbString(storedStatus!); // throws if it is not a documented value

        Assert.Equal("manual", await ScalarAsync("SELECT triggered_by_type FROM runs WHERE id = @Id", new { Id = run.Id }));
    }

    /// <summary>
    /// Nothing anywhere in the database may still hold the ordinal encoding. This is the
    /// whole-table version of the assertions above and also proves the 0004 backfill converted the
    /// rows written before the fix.
    /// </summary>
    [Theory]
    [InlineData("runs", "status")]
    [InlineData("runs", "triggered_by_type")]
    [InlineData("approvals", "status")]
    [InlineData("approvals", "approval_type")]
    [InlineData("agents", "agent_type")]
    [InlineData("users", "role")]
    [InlineData("secrets", "scope")]
    public async Task No_row_in_any_enum_column_holds_an_ordinal(string table, string column)
    {
        var offenders = await ScalarAsync($"SELECT COUNT(*) FROM {table} WHERE {column} ~ '^[0-9]+$'", new { });
        Assert.Equal("0", offenders);
    }

    // ---------------------------------------------------------------- round-trips

    [Fact]
    public async Task Every_RunStatus_round_trips_through_the_database()
    {
        var fixture = await CreateFixtureAsync();
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();

        var run = await InsertRunAsync(runs, fixture, RunStatus.Pending, TriggeredByType.Manual);

        foreach (var status in Enum.GetValues<RunStatus>())
        {
            run.Status = status;
            run.UpdatedAt = DateTime.UtcNow;
            await runs.UpdateAsync(run);

            var reloaded = await runs.GetAsync(run.Id);
            Assert.Equal(status, reloaded!.Status);
            Assert.Equal(status.ToDbString(), await ScalarAsync("SELECT status FROM runs WHERE id = @Id", new { Id = run.Id }));
        }
    }

    [Fact]
    public async Task Every_TriggeredByType_round_trips_through_the_database()
    {
        var fixture = await CreateFixtureAsync();
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();

        foreach (var trigger in Enum.GetValues<TriggeredByType>())
        {
            var run = await InsertRunAsync(runs, fixture, RunStatus.Queued, trigger);

            var reloaded = await runs.GetAsync(run.Id);
            Assert.Equal(trigger, reloaded!.TriggeredByType);
            Assert.Equal(trigger.ToDbString(), await ScalarAsync("SELECT triggered_by_type FROM runs WHERE id = @Id", new { Id = run.Id }));
        }
    }

    [Fact]
    public async Task Every_AgentType_round_trips_through_the_database()
    {
        var fixture = await CreateFixtureAsync();
        using var scope = _factory.Services.CreateScope();
        var agents = scope.ServiceProvider.GetRequiredService<IAgentRepository>();

        foreach (var type in Enum.GetValues<AgentType>())
        {
            var suffix = TestData.Suffix();
            var agent = new Agent
            {
                Id = UlidGenerator.NewUlid(),
                OrgId = fixture.OrgId,
                ProjectId = fixture.ProjectId,
                Name = $"Round trip {suffix}",
                Slug = $"round-trip-{suffix}",
                AgentType = type,
                ManifestYaml = TestData.BuildManifestYaml($"round-trip-{suffix}"),
                InputsSchema = "{}",
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await agents.InsertAsync(agent);

            var reloaded = await agents.GetAsync(agent.Id);
            Assert.Equal(type, reloaded!.AgentType);
            Assert.Equal(type.ToDbString(), await ScalarAsync("SELECT agent_type FROM agents WHERE id = @Id", new { Id = agent.Id }));
        }
    }

    [Fact]
    public async Task Every_ApprovalType_and_ApprovalStatus_round_trips_through_the_database()
    {
        var fixture = await CreateFixtureAsync();
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var approvals = scope.ServiceProvider.GetRequiredService<IApprovalRepository>();

        var run = await InsertRunAsync(runs, fixture, RunStatus.AwaitingApproval, TriggeredByType.Manual);

        foreach (var type in Enum.GetValues<ApprovalType>())
        {
            foreach (var status in Enum.GetValues<ApprovalStatus>())
            {
                var approval = new Approval
                {
                    Id = UlidGenerator.NewUlid(),
                    RunId = run.Id,
                    ApprovalType = type,
                    Prompt = "Round trip?",
                    Status = status,
                    ExpiresAt = DateTime.UtcNow.AddHours(1),
                    CreatedAt = DateTime.UtcNow,
                };
                await approvals.InsertAsync(approval);

                var reloaded = await approvals.GetAsync(approval.Id, fixture.OrgId);
                Assert.Equal(type, reloaded!.ApprovalType);
                Assert.Equal(status, reloaded.Status);
                Assert.Equal(type.ToDbString(), await ScalarAsync("SELECT approval_type FROM approvals WHERE id = @Id", new { Id = approval.Id }));
                Assert.Equal(status.ToDbString(), await ScalarAsync("SELECT status FROM approvals WHERE id = @Id", new { Id = approval.Id }));
            }
        }

        // ...and the SQL-side 'pending' predicate finds the pending one among them.
        var pending = await approvals.GetPendingForRunAsync(run.Id);
        Assert.NotNull(pending);
        Assert.Equal(ApprovalStatus.Pending, pending!.Status);
    }

    [Fact]
    public async Task Every_UserRole_round_trips_through_the_database()
    {
        var fixture = await CreateFixtureAsync();
        using var scope = _factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<IUserRepository>();

        foreach (var role in Enum.GetValues<UserRole>())
        {
            var user = new User
            {
                Id = UlidGenerator.NewUlid(),
                OrgId = fixture.OrgId,
                Email = $"{role.ToDbString()}-{TestData.Suffix()}@roundtrip.test",
                DisplayName = "Round trip",
                Role = role,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await users.InsertAsync(user);

            var reloaded = await users.GetAsync(user.Id);
            Assert.Equal(role, reloaded!.Role);
            Assert.Equal(role.ToDbString(), await ScalarAsync("SELECT role FROM users WHERE id = @Id", new { Id = user.Id }));
        }
    }

    [Fact]
    public async Task Every_SecretScope_round_trips_through_the_database()
    {
        var fixture = await CreateFixtureAsync();
        using var scope = _factory.Services.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretRepository>();

        foreach (var secretScope in Enum.GetValues<SecretScope>())
        {
            var secret = new Secret
            {
                Id = UlidGenerator.NewUlid(),
                OrgId = fixture.OrgId,
                ProjectId = secretScope == SecretScope.Project ? fixture.ProjectId : null,
                Name = $"ROUNDTRIP_{secretScope.ToDbString().ToUpperInvariant()}_{TestData.Suffix()}",
                EncryptedValue = new byte[] { 1, 2, 3 },
                Scope = secretScope,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };
            await secrets.InsertAsync(secret);

            var reloaded = await secrets.GetAsync(secret.Id, fixture.OrgId);
            Assert.Equal(secretScope, reloaded!.Scope);
            Assert.Equal(secretScope.ToDbString(), await ScalarAsync("SELECT scope FROM secrets WHERE id = @Id", new { Id = secret.Id }));
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed record Fixture(string OrgId, string ProjectId, string AgentId, string AgentVersionId);

    /// <summary>Org + project + published agent, created over HTTP, for repository-level tests.</summary>
    private async Task<Fixture> CreateFixtureAsync()
    {
        var (_, auth, project, agent) = await TestData.CreateFullFixtureAsync(_factory);
        var agentVersionId = await ScalarAsync(
            "SELECT id FROM agent_versions WHERE agent_id = @AgentId ORDER BY created_at DESC LIMIT 1",
            new { AgentId = agent.Id });
        Assert.NotNull(agentVersionId);
        return new Fixture(auth.User.OrgId, project.Id, agent.Id, agentVersionId!);
    }

    private static async Task<Run> InsertRunAsync(
        IRunRepository runs, Fixture fixture, RunStatus status, TriggeredByType trigger)
    {
        var run = new Run
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = fixture.OrgId,
            ProjectId = fixture.ProjectId,
            Number = await runs.GetNextRunNumberAsync(fixture.ProjectId),
            AgentId = fixture.AgentId,
            AgentVersionId = fixture.AgentVersionId,
            Status = status,
            Inputs = new JsonObject(),
            Context = new JsonObject(),
            TriggeredByType = trigger,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await runs.InsertAsync(run);
        return run;
    }

    private static async Task<string> CreateSecretAsync(
        HttpClient client, string name, string value, SecretScope scope, string? projectId)
    {
        var response = await client.PostJsonAsync("/api/secrets", new CreateSecretRequest
        {
            Name = name,
            Value = value,
            Scope = scope,
            ProjectId = projectId,
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SecretResponse>(TestJson.Options);
        return body!.Id;
    }

    private static async Task<Agent> CreateAgentAsync(HttpClient client, string projectId, string suffix, string manifestYaml)
    {
        var response = await client.PostJsonAsync("/api/agents", new CreateAgentRequest
        {
            ProjectId = projectId,
            Name = $"Secret Agent {suffix}",
            Slug = $"secret-agent-{suffix}",
            ManifestYaml = manifestYaml,
            Publish = true,
        });
        response.EnsureSuccessStatusCode();
        var agent = await response.Content.ReadFromJsonAsync<Agent>(TestJson.Options);
        return agent!;
    }

    /// <summary>A minimal valid manifest that declares the given secret names in permissions.secrets.</summary>
    private static string ManifestWithSecrets(string name, params string[] secretNames)
    {
        var declared = string.Join("\n", secretNames.Select(s => $"      - {s}"));
        return $"""
            apiVersion: agenthost.dev/v1
            kind: Agent
            metadata:
              name: {name}
              displayName: {name}
              description: Integration test agent
            spec:
              type: oci
              image: busybox:latest
              permissions:
                vcs: none
                network: none
                secrets:
            {declared}
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
    }

    /// <summary>Reads a single value straight out of Postgres, bypassing the repositories entirely.</summary>
    private async Task<string?> ScalarAsync(string sql, object parameters)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var db = factory.CreateConnection();
        var value = await db.ExecuteScalarAsync<object?>(sql, parameters);
        return value?.ToString();
    }
}
