using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Coverage of the RunHub over a real SignalR connection.
///
/// This file exists because of a bug it would have caught: <c>ICallerContext</c> resolves the
/// caller from <see cref="IHttpContextAccessor"/>, whose HttpContext is null during a hub method
/// invocation (it only exists for the initial negotiate request). Everything reachable from a hub
/// method therefore saw an unauthenticated caller, so <c>RunService.ApproveAsync</c> — which
/// refuses a decision it cannot attribute — failed closed on exactly the role-gated approvals the
/// approvals feature exists for, and attributed the rest to "unknown". Hubs/CallerContextHubFilter
/// binds the principal per invocation; the tests below pin both halves of that behaviour.
///
/// Transport is forced to long polling: TestServer has no real socket, and long polling exercises
/// the same hub dispatch path (including hub filters) without needing one.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class RunHubTests : IAsyncLifetime
{
    private readonly AgentHostApiFactory _factory;

    private HttpClient _owner = default!;
    private AuthResponse _auth = default!;
    private Agent _agent = default!;

    public RunHubTests(AgentHostApiFactory factory) => _factory = factory;

    public async Task InitializeAsync()
    {
        (_owner, _auth, _, _agent) = await TestData.CreateFullFixtureAsync(_factory, TestData.Suffix());
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ApproveStep_OnARoleGatedApproval_IsAttributedToTheConnectedUser()
    {
        var run = await CreateParkedRunAsync();
        // requiredRole "maintainer" — the connected user is an owner, so they outrank it. Before the
        // hub filter existed this still failed, because the caller could not be identified at all.
        var approvalId = await CreatePendingApprovalAsync(run, requiredRole: "maintainer");

        await using var connection = await ConnectAsync(_auth.Token);
        var errors = CaptureErrors(connection);

        await connection.InvokeAsync("JoinRun", run.Id);
        await connection.InvokeAsync("ApproveStep", run.Id, "step-1", "approved over the hub");

        await errors.AssertNoneWithinAsync(TimeSpan.FromMilliseconds(500));

        var approval = await GetApprovalAsync(approvalId, run.OrgId);
        Assert.Equal(ApprovalStatus.Approved, approval.Status);
        // The decider is the connected user, not null and not "unknown".
        Assert.Equal(_auth.User.Id, approval.DecidedBy);
        Assert.Equal(_auth.User.Id, Assert.Single(approval.Responses).By);

        Assert.Equal(RunStatus.Running, (await GetRunAsync(run.Id)).Status);
    }

    [Fact]
    public async Task ApproveStep_ByAUserBelowTheRequiredRole_IsRefused()
    {
        var run = await CreateParkedRunAsync();
        var approvalId = await CreatePendingApprovalAsync(run, requiredRole: "owner");

        // A developer passes the hub's org check but is below the approval's required role.
        var (developerToken, _) = await TestData.CreateUserWithRoleAsync(_owner, UserRole.Developer);

        await using var connection = await ConnectAsync(developerToken);
        var errors = CaptureErrors(connection);

        await connection.InvokeAsync("JoinRun", run.Id);
        await connection.InvokeAsync("ApproveStep", run.Id, "step-1", null);

        var received = await errors.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(received, e => e.Contains("Approval failed", StringComparison.OrdinalIgnoreCase));

        var approval = await GetApprovalAsync(approvalId, run.OrgId);
        Assert.Equal(ApprovalStatus.Pending, approval.Status);
        Assert.Null(approval.DecidedBy);
        Assert.Equal(RunStatus.AwaitingApproval, (await GetRunAsync(run.Id)).Status);
    }

    [Fact]
    public async Task JoinRun_ForAnotherOrgsRun_IsRefusedAndRevealsNothing()
    {
        var run = await CreateParkedRunAsync();

        // A completely separate tenant.
        var otherAuth = await TestData.RegisterAsync(_factory.CreateClient());

        await using var connection = await ConnectAsync(otherAuth.Token);
        var errors = CaptureErrors(connection);
        var states = new List<Run>();
        connection.On<Run>("runState", state => states.Add(state));

        await connection.InvokeAsync("JoinRun", run.Id);

        // Same wording as a genuinely missing run, so the hub is not an existence oracle.
        var received = await errors.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(received, e => e.Contains("not found", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(states);
    }

    [Fact]
    public async Task JoinRun_ForOwnRun_ReceivesTheRunState()
    {
        var run = await CreateParkedRunAsync();

        await using var connection = await ConnectAsync(_auth.Token);
        var errors = CaptureErrors(connection);

        var received = new TaskCompletionSource<Run>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<Run>("runState", state => received.TrySetResult(state));

        await connection.InvokeAsync("JoinRun", run.Id);

        var state = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(run.Id, state.Id);
        await errors.AssertNoneWithinAsync(TimeSpan.FromMilliseconds(250));
    }

    // ---- helpers ----

    private async Task<HubConnection> ConnectAsync(string accessToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress, "hubs/run"), options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            // Mirrors Program.cs's server-side AddJsonProtocol. Without the enum converters this
            // .NET client cannot turn "running" back into RunStatus and drops the payload silently,
            // which no real (TypeScript) client would hit — the browser never deserializes into a
            // CLR enum. Purely a test-harness concern.
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNameCaseInsensitive = true;
                options.PayloadSerializerOptions.Converters.Add(new RunStatusJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new AgentTypeJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new TriggeredByTypeJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new ApprovalTypeJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new ApprovalStatusJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new SecretScopeJsonConverter());
                options.PayloadSerializerOptions.Converters.Add(new UserRoleJsonConverter());
            })
            .Build();

        await connection.StartAsync();
        return connection;
    }

    /// <summary>
    /// Collects everything the hub sends on its "error" channel.
    ///
    /// The hub reports failures by sending a separate <c>error</c> message rather than by faulting
    /// the invocation, so <see cref="HubConnection.InvokeAsync(string, object?[])"/> returns before
    /// that message necessarily arrives. Assertions must therefore wait for an error
    /// (<see cref="ErrorSink.WaitAsync"/>) or wait out a grace period to assert its absence
    /// (<see cref="ErrorSink.AssertNoneWithinAsync"/>) — never read the list immediately.
    /// </summary>
    private static ErrorSink CaptureErrors(HubConnection connection)
    {
        var sink = new ErrorSink();
        connection.On<string>("error", sink.Add);
        return sink;
    }

    private sealed class ErrorSink
    {
        private readonly List<string> _messages = new();
        private readonly SemaphoreSlim _received = new(0);

        public void Add(string message)
        {
            lock (_messages) _messages.Add(message);
            _received.Release();
        }

        private IReadOnlyList<string> Snapshot()
        {
            lock (_messages) return _messages.ToArray();
        }

        /// <summary>Waits for at least one error, then returns everything seen so far.</summary>
        public async Task<IReadOnlyList<string>> WaitAsync(TimeSpan timeout)
        {
            Assert.True(await _received.WaitAsync(timeout), "Expected the hub to send an error, but none arrived");
            return Snapshot();
        }

        /// <summary>Asserts no error arrives within the grace period.</summary>
        public async Task AssertNoneWithinAsync(TimeSpan grace)
        {
            if (await _received.WaitAsync(grace))
                Assert.Fail($"Expected no hub error, got: {string.Join(", ", Snapshot())}");
        }
    }

    private async Task<Run> CreateParkedRunAsync()
    {
        var response = await _owner.PostJsonAsync("/api/runs", new CreateRunRequest { AgentId = _agent.Id });
        response.EnsureSuccessStatusCode();
        var run = await response.Content.ReadFromJsonAsync<Run>(TestJson.Options);
        Assert.NotNull(run);

        // Docker is unreachable here, so the background launch fails into a terminal state; wait for
        // that to settle before parking the run, so nothing races the test. Same approach as
        // AgentProtocolEndpointsTests.
        using var scope = _factory.Services.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IRunRepository>();

        Run? settled = null;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            settled = await repository.GetAsync(run!.Id);
            if (settled is not null && settled.Status.IsTerminal()) break;
            await Task.Delay(25);
        }

        Assert.NotNull(settled);
        Assert.True(settled!.Status.IsTerminal(), $"Run {run!.Id} never settled (still {settled.Status})");
        return settled;
    }

    /// <summary>
    /// Parks the run in <c>awaiting_approval</c> with a pending approval, which is the state a hub
    /// approval acts on. Written directly through the repositories rather than through the agent
    /// protocol so this file tests the hub and nothing else.
    /// </summary>
    private async Task<string> CreatePendingApprovalAsync(Run run, string requiredRole)
    {
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var approvals = scope.ServiceProvider.GetRequiredService<IApprovalRepository>();

        var approval = new Approval
        {
            Id = UlidGenerator.NewUlid(),
            RunId = run.Id,
            StepId = "step-1",
            ApprovalType = ApprovalType.Gate,
            Prompt = "May I push to the default branch?",
            RequiredRole = requiredRole,
            RequiredCount = 1,
            Status = ApprovalStatus.Pending,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            CreatedAt = DateTime.UtcNow,
        };
        await approvals.InsertAsync(approval);

        var stored = await runs.GetAsync(run.Id);
        stored!.Status = RunStatus.AwaitingApproval;
        stored.StartedAt = DateTime.UtcNow;
        stored.FinishedAt = null;
        stored.ErrorCode = null;
        stored.ErrorMessage = null;
        stored.UpdatedAt = DateTime.UtcNow;
        await runs.UpdateAsync(stored);

        return approval.Id;
    }

    private async Task<Approval> GetApprovalAsync(string approvalId, string orgId)
    {
        using var scope = _factory.Services.CreateScope();
        var approvals = scope.ServiceProvider.GetRequiredService<IApprovalRepository>();
        var approval = await approvals.GetAsync(approvalId, orgId);
        Assert.NotNull(approval);
        return approval!;
    }

    private async Task<Run> GetRunAsync(string runId)
    {
        using var scope = _factory.Services.CreateScope();
        var runs = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var run = await runs.GetAsync(runId);
        Assert.NotNull(run);
        return run!;
    }
}
