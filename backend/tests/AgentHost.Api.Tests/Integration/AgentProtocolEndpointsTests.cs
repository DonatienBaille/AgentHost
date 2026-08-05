using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// HTTP-level coverage of the agent → host callback protocol (/api/agent/..., see
/// docs/agent-protocol.md): event streaming, output publishing, approvals/questions, usage
/// reporting with budget enforcement, and the run-scoping of the token itself.
///
/// Docker is unreachable in this sandbox, so every run's background launch fails and lands in
/// infra_error shortly after creation. The run row itself is inserted synchronously by
/// RunService.CreateAsync, so the tests wait for that background attempt to settle and then park the
/// run in <c>running</c> directly through IRunRepository — arranging the exact lifecycle state the
/// protocol is specified against, with no background writer left racing them.
///
/// The org/project/agent fixture is created once for the whole class (IAsyncLifetime) to keep the
/// number of HTTP calls — and therefore pressure on the shared global rate limiter — low.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class AgentProtocolEndpointsTests : IAsyncLifetime
{
    private readonly AgentHostApiFactory _factory;

    private HttpClient _owner = default!;
    private AuthResponse _auth = default!;
    private Project _project = default!;
    private Agent _agent = default!;

    public AgentProtocolEndpointsTests(AgentHostApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        var suffix = TestData.Suffix();
        (_owner, _auth, _project, _agent) = await TestData.CreateFullFixtureAsync(_factory, suffix);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ---- protocol happy paths ----

    [Fact]
    public async Task Agent_PostEvent_PersistsToRunEventsAndIsReadableByAHuman()
    {
        var run = await CreateRunAsync();
        await ParkRunAsRunningAsync(run.Id);
        var agent = AgentClient(run);

        var response = await agent.PostJsonAsync($"/api/agent/runs/{run.Id}/events", new AgentEventRequest
        {
            EventType = "tool.called",
            Level = "info",
            Message = "ran the linter",
            Payload = new JsonObject { ["tool"] = "eslint", ["files"] = 3 },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<AgentEventResponse>(TestJson.Options);
        Assert.NotNull(body);
        Assert.True(body!.Seq > 0);

        // The same event is visible on the human-facing run event stream.
        var events = await _owner.GetFromJsonAsync<List<RunEvent>>($"/api/runs/{run.Id}/events", TestJson.Options);
        Assert.NotNull(events);
        var persisted = Assert.Single(events!, e => e.EventType == "tool.called");
        Assert.Equal("ran the linter", persisted.Message);
    }

    [Fact]
    public async Task Agent_PostOutputs_WritesRunOutputs()
    {
        var run = await CreateRunAsync();
        await ParkRunAsRunningAsync(run.Id);
        var agent = AgentClient(run);

        var response = await agent.PostJsonAsync($"/api/agent/runs/{run.Id}/outputs", new AgentOutputsRequest
        {
            Outputs = new JsonObject { ["pullRequestUrl"] = "https://example.test/pr/1", ["filesChanged"] = 7 },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var stored = await _owner.GetFromJsonAsync<Run>($"/api/runs/{run.Id}", TestJson.Options);
        Assert.NotNull(stored?.Outputs);
        Assert.Equal("https://example.test/pr/1", stored!.Outputs!["pullRequestUrl"]!.GetValue<string>());
        Assert.Equal(7, stored.Outputs["filesChanged"]!.GetValue<int>());
    }

    [Fact]
    public async Task Agent_RequestsApproval_HumanApproves_RunResumes()
    {
        var run = await CreateRunAsync();
        await ParkRunAsRunningAsync(run.Id);
        var agent = AgentClient(run);

        var requested = await agent.PostJsonAsync($"/api/agent/runs/{run.Id}/approvals", new AgentApprovalRequest
        {
            Prompt = "May I push to the default branch?",
            StepId = "step-7",
        });

        Assert.Equal(HttpStatusCode.Created, requested.StatusCode);
        var created = await requested.Content.ReadFromJsonAsync<AgentApprovalCreatedResponse>(TestJson.Options);
        Assert.NotNull(created);
        Assert.Equal("pending", created!.Status);
        Assert.Equal("awaiting_approval", created.RunStatus);

        // The run is parked waiting for a human, and the agent can see that by polling.
        Assert.Equal(RunStatus.AwaitingApproval, (await GetRunAsync(run.Id)).Status);

        var polled = await agent.GetFromJsonAsync<AgentApprovalStatusResponse>(
            $"/api/agent/runs/{run.Id}/approvals/{created.ApprovalId}", TestJson.Options);
        Assert.Equal("pending", polled!.Status);

        // A human approves through the existing user-facing endpoint.
        var approved = await _owner.PostJsonAsync($"/api/runs/{run.Id}/approve", new ApprovalRequest
        {
            StepId = "step-7",
            Decision = "approve",
            Note = "looks good",
        });
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);

        Assert.Equal(RunStatus.Running, (await GetRunAsync(run.Id)).Status);

        var afterDecision = await agent.GetFromJsonAsync<AgentApprovalStatusResponse>(
            $"/api/agent/runs/{run.Id}/approvals/{created.ApprovalId}", TestJson.Options);
        Assert.Equal("approved", afterDecision!.Status);
        Assert.Equal("running", afterDecision.RunStatus);
        Assert.Equal(_auth.User.Id, afterDecision.DecidedBy);
        Assert.Equal("looks good", afterDecision.Note);
    }

    [Fact]
    public async Task Agent_AsksQuestion_HumanAnswers_RunResumes()
    {
        var run = await CreateRunAsync();
        await ParkRunAsRunningAsync(run.Id);
        var agent = AgentClient(run);

        var asked = await agent.PostJsonAsync($"/api/agent/runs/{run.Id}/questions", new AgentQuestionRequest
        {
            Prompt = "Which database should I target?",
            Options = new JsonArray("postgres", "mysql"),
        });

        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        var created = await asked.Content.ReadFromJsonAsync<AgentApprovalCreatedResponse>(TestJson.Options);
        Assert.Equal("awaiting_input", created!.RunStatus);
        Assert.Equal(RunStatus.AwaitingInput, (await GetRunAsync(run.Id)).Status);

        var answered = await _owner.PostJsonAsync($"/api/runs/{run.Id}/answer", new AnswerQuestionRequest
        {
            QuestionId = created.ApprovalId,
            Answer = "postgres",
        });
        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);

        Assert.Equal(RunStatus.Running, (await GetRunAsync(run.Id)).Status);

        var polled = await agent.GetFromJsonAsync<AgentApprovalStatusResponse>(
            $"/api/agent/runs/{run.Id}/approvals/{created.ApprovalId}", TestJson.Options);
        Assert.Equal("approved", polled!.Status);
        Assert.Equal("postgres", polled.Answer);
    }

    [Fact]
    public async Task Agent_ReportsUsage_AccumulatesAndTripsBudgetExceeded()
    {
        // The test manifest sets budget.defaultMaxUsd = 5, so the run's cap is 5 USD.
        var run = await CreateRunAsync();
        await ParkRunAsRunningAsync(run.Id);
        var agent = AgentClient(run);

        var first = await agent.PostJsonAsync($"/api/agent/runs/{run.Id}/usage", new AgentUsageRequest
        {
            CostUsd = 2.50m,
            TokensIn = 1000,
            TokensOut = 400,
            Model = "test-model",
        });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<AgentUsageResponse>(TestJson.Options);
        Assert.Equal(2.50m, firstBody!.BudgetUsedUsd);
        Assert.False(firstBody.BudgetExceeded);
        Assert.Equal(RunStatus.Running, (await GetRunAsync(run.Id)).Status);

        // Second report accumulates on top of the first (atomic += in SQL) and blows the budget.
        var second = await agent.PostJsonAsync($"/api/agent/runs/{run.Id}/usage", new AgentUsageRequest
        {
            CostUsd = 3.00m,
        });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<AgentUsageResponse>(TestJson.Options);
        Assert.Equal(5.50m, secondBody!.BudgetUsedUsd);
        Assert.Equal(5m, secondBody.BudgetMaxUsd);
        Assert.True(secondBody.BudgetExceeded);

        var stored = await GetRunAsync(run.Id);
        Assert.Equal(RunStatus.BudgetExceeded, stored.Status);
        Assert.Equal(5.50m, stored.BudgetUsedUsd);
        Assert.Equal("budget_exceeded", stored.ErrorCode);
    }

    // ---- security: a run token is authoritative for exactly one run ----

    [Fact]
    public async Task AgentToken_ForAnotherRun_CannotTouchThisRun()
    {
        var runA = await CreateRunAsync();
        var runB = await CreateRunAsync();
        await ParkRunAsRunningAsync(runA.Id);
        await ParkRunAsRunningAsync(runB.Id);

        var agentA = AgentClient(runA);

        // Every endpoint must reject a token minted for a different run.
        var postEvent = await agentA.PostJsonAsync($"/api/agent/runs/{runB.Id}/events", new AgentEventRequest
        {
            EventType = "log",
            Message = "cross-run write attempt",
        });
        Assert.Equal(HttpStatusCode.Forbidden, postEvent.StatusCode);

        var postOutputs = await agentA.PostJsonAsync($"/api/agent/runs/{runB.Id}/outputs", new AgentOutputsRequest
        {
            Outputs = new JsonObject { ["stolen"] = true },
        });
        Assert.Equal(HttpStatusCode.Forbidden, postOutputs.StatusCode);

        var postApproval = await agentA.PostJsonAsync($"/api/agent/runs/{runB.Id}/approvals", new AgentApprovalRequest
        {
            Prompt = "let me through",
        });
        Assert.Equal(HttpStatusCode.Forbidden, postApproval.StatusCode);

        var postUsage = await agentA.PostJsonAsync($"/api/agent/runs/{runB.Id}/usage", new AgentUsageRequest
        {
            CostUsd = 99m,
        });
        Assert.Equal(HttpStatusCode.Forbidden, postUsage.StatusCode);

        // Run B is untouched: still running, no budget spent, no outputs.
        var storedB = await GetRunAsync(runB.Id);
        Assert.Equal(RunStatus.Running, storedB.Status);
        Assert.Equal(0m, storedB.BudgetUsedUsd ?? 0m);
        Assert.Null(storedB.Outputs);
    }

    [Fact]
    public async Task UserToken_IsRejectedByTheAgentProtocol_AndRunTokenByTheUserApi()
    {
        var run = await CreateRunAsync();

        // A human's normal JWT has the wrong audience for the AgentRun scheme.
        var asHuman = await _owner.PostJsonAsync($"/api/agent/runs/{run.Id}/events", new AgentEventRequest
        {
            EventType = "log",
            Message = "humans do not speak this protocol",
        });
        Assert.Equal(HttpStatusCode.Unauthorized, asHuman.StatusCode);

        // And the reverse: a run token is not a user token.
        var asAgent = await AgentClient(run).GetAsync($"/api/runs/{run.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, asAgent.StatusCode);
    }

    [Fact]
    public async Task Approval_RequiredRole_RejectsADecisionFromALowerRole()
    {
        var run = await CreateRunAsync();
        await ParkRunAsRunningAsync(run.Id);
        var agent = AgentClient(run);

        var requested = await agent.PostJsonAsync($"/api/agent/runs/{run.Id}/approvals", new AgentApprovalRequest
        {
            Prompt = "Deploy to production?",
            RequiredRole = "maintainer",
        });
        Assert.Equal(HttpStatusCode.Created, requested.StatusCode);

        // The org is no longer passed explicitly — the new user lands in the caller's own org,
        // taken from the owner client's JWT.
        var (developerToken, _) = await TestData.CreateUserWithRoleAsync(_owner, UserRole.Developer);
        var developer = TestData.AuthedClient(_factory, developerToken);

        // The developer passes the endpoint's RBAC policy but is below the approval's required role.
        var byDeveloper = await developer.PostJsonAsync($"/api/runs/{run.Id}/approve", new ApprovalRequest
        {
            Decision = "approve",
        });
        Assert.Equal(HttpStatusCode.BadRequest, byDeveloper.StatusCode);
        Assert.Equal(RunStatus.AwaitingApproval, (await GetRunAsync(run.Id)).Status);

        // The owner outranks maintainer, so their decision is accepted.
        var byOwner = await _owner.PostJsonAsync($"/api/runs/{run.Id}/approve", new ApprovalRequest
        {
            Decision = "approve",
        });
        Assert.Equal(HttpStatusCode.OK, byOwner.StatusCode);
        Assert.Equal(RunStatus.Running, (await GetRunAsync(run.Id)).Status);
    }

    // ---- budget guard rails at run creation ----

    [Fact]
    public async Task CreateRun_WithBudgetAboveTheManifestHardMax_Is422()
    {
        // The test manifest's budget.hardMaxUsd is 10; a client must not be able to grant itself more.
        var response = await _owner.PostJsonAsync("/api/runs", new CreateRunRequest
        {
            AgentId = _agent.Id,
            BudgetMaxUsd = 500m,
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task CreateRun_WithBudgetWithinTheManifestHardMax_IsAccepted()
    {
        var response = await _owner.PostJsonAsync("/api/runs", new CreateRunRequest
        {
            AgentId = _agent.Id,
            BudgetMaxUsd = 9m,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<Run>(TestJson.Options);
        Assert.Equal(9m, run!.BudgetMaxUsd);
        // The run is attributed to the authenticated caller, not to anything in the request body.
        Assert.Equal(_auth.User.Id, run.TriggeredByUserId);
    }

    // ---- watchdog ----

    [Fact]
    public async Task Watchdog_TimesOutARunPastItsMaxDuration()
    {
        var run = await CreateRunAsync();
        // The test manifest allows 300s; start the run an hour ago so it is unambiguously overdue.
        await ParkRunAsRunningAsync(run.Id, DateTime.UtcNow.AddHours(-1));

        await _factory.Services.GetRequiredService<RunWatchdog>().ScanOnceAsync();

        var stored = await GetRunAsync(run.Id);
        Assert.Equal(RunStatus.TimedOut, stored.Status);
        Assert.Equal("timeout", stored.ErrorCode);
    }

    [Fact]
    public async Task Watchdog_ExpiresAPendingApproval_AndTimesOutItsRun()
    {
        var run = await CreateRunAsync();
        await ParkRunAsRunningAsync(run.Id);
        var agent = AgentClient(run);

        var requested = await agent.PostJsonAsync($"/api/agent/runs/{run.Id}/approvals", new AgentApprovalRequest
        {
            Prompt = "Nobody is going to answer this",
            ExpiresInSeconds = 1,
        });
        var created = await requested.Content.ReadFromJsonAsync<AgentApprovalCreatedResponse>(TestJson.Options);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));
        await _factory.Services.GetRequiredService<RunWatchdog>().ScanOnceAsync();

        var polled = await agent.GetFromJsonAsync<AgentApprovalStatusResponse>(
            $"/api/agent/runs/{run.Id}/approvals/{created!.ApprovalId}", TestJson.Options);
        Assert.Equal("expired", polled!.Status);

        var stored = await GetRunAsync(run.Id);
        Assert.Equal(RunStatus.TimedOut, stored.Status);
        Assert.Equal("approval_expired", stored.ErrorCode);
    }

    // ---- helpers ----

    private async Task<Run> CreateRunAsync()
    {
        var response = await _owner.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = _agent.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<Run>(TestJson.Options);
        Assert.NotNull(run);
        return run!;
    }

    private async Task<Run> GetRunAsync(string runId)
    {
        var run = await _owner.GetFromJsonAsync<Run>($"/api/runs/{runId}", TestJson.Options);
        Assert.NotNull(run);
        return run!;
    }

    /// <summary>
    /// Waits for the sandbox's doomed container launch to settle, then parks the run in
    /// <c>running</c> — the state the protocol is specified against — with no background writer
    /// left to race the test.
    /// </summary>
    private async Task ParkRunAsRunningAsync(string runId, DateTime? startedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRunRepository>();

        Run? run = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            run = await repository.GetAsync(runId);
            if (run is not null && run.Status.IsTerminal()) break;
            await Task.Delay(25);
        }

        Assert.NotNull(run);
        Assert.True(run!.Status.IsTerminal(), $"Run {runId} never settled (still {run.Status})");

        run.Status = RunStatus.Running;
        run.StartedAt = startedAt ?? DateTime.UtcNow;
        run.FinishedAt = null;
        run.ErrorCode = null;
        run.ErrorMessage = null;
        run.UpdatedAt = DateTime.UtcNow;
        await repository.UpdateAsync(run);
    }

    /// <summary>An HttpClient authenticated exactly as the agent container is: with AGENTHOST_RUN_TOKEN.</summary>
    private HttpClient AgentClient(Run run)
    {
        var tokenService = _factory.Services.GetRequiredService<IRunTokenService>();
        var token = tokenService.Issue(run.Id, run.ProjectId, run.OrgId, 3600);

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
