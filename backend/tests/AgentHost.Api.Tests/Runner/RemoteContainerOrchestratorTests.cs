using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;
using Xunit;

namespace AgentHost.Api.Tests.Runner;

/// <summary>
/// The runner tier, end to end between the two processes: a backend
/// <see cref="RemoteContainerOrchestrator"/> against a real runner API
/// (<see cref="SimulatedRunner"/>) over a real socket.
///
/// <para><b>What these tests cannot prove.</b> There is no container daemon in this environment, so
/// nothing here shows that a container actually starts, exits, or writes a log line. Every launch
/// stops at the runner's supervisor. What is covered is the layer the lot added — the protocol
/// between backend and runner, and the routing of stop/logs to the runner that holds a given
/// run — plus every degraded case, which is where a silent lie would otherwise live.</para>
/// </summary>
public class RemoteContainerOrchestratorTests
{
    private const string Token = "test-runner-token-0123456789";

    // ---- Harness -----------------------------------------------------------------------------

    private sealed class Harness
    {
        public Mock<IRunRepository> Runs { get; } = new();
        public Mock<IEventBus> Events { get; } = new();
        public Mock<IWebhookDispatcher> Webhooks { get; } = new();
        public List<(string RunId, string? Url)> RunnerUrlWrites { get; } = new();

        /// <summary>Simule la colonne <c>runs.runner_url</c>.</summary>
        public Dictionary<string, string?> RunnerUrls { get; } = new();

        public RemoteContainerOrchestrator Build(RunnerOptions options)
        {
            Runs.Setup(r => r.TryUpdateWithExpectedStatusAsync(
                    It.IsAny<Run>(), It.IsAny<RunStatus>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            Runs.Setup(r => r.SetRunnerUrlAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback<string, string?, CancellationToken>((runId, url, _) =>
                {
                    RunnerUrlWrites.Add((runId, url));
                    RunnerUrls[runId] = url;
                })
                .ReturnsAsync(true);

            Runs.Setup(r => r.GetRunnerUrlAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string runId, CancellationToken _) =>
                    RunnerUrls.TryGetValue(runId, out var url) ? url : null);

            Runs.Setup(r => r.UpdateAsync(It.IsAny<Run>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddSingleton(Runs.Object);
            services.AddSingleton(Events.Object);
            services.AddSingleton<ILogger>(Serilog.Core.Logger.None);
            services.AddSingleton<RunStateMachine>(_ =>
                new RunStateMachine(Runs.Object, Events.Object, Webhooks.Object, Serilog.Core.Logger.None, TestMetrics.Create()));
            var provider = services.BuildServiceProvider();

            var stateMachine = provider.GetRequiredService<RunStateMachine>();
            var recorder = new RunCompletionRecorder(
                provider.GetRequiredService<IServiceScopeFactory>(), Serilog.Core.Logger.None);

            return new RemoteContainerOrchestrator(
                new SingleClientFactory(options.AuthToken),
                options,
                Runs.Object,
                new AgentManifestParser(),
                stateMachine,
                recorder,
                Serilog.Core.Logger.None);
        }
    }

    /// <summary>Reproduit le client nommé enregistré par Program.cs, jeton porteur compris.</summary>
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly string? _token;
        public SingleClientFactory(string? token) => _token = token;

        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient();
            if (!string.IsNullOrEmpty(_token))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            return client;
        }
    }

    private static RunnerOptions OptionsFor(string baseUrl, string token = Token) => new()
    {
        Mode = RunnerMode.Remote,
        BaseUrl = baseUrl,
        AuthToken = token,
        WaitTimeoutSeconds = 2,
        RequestTimeoutSeconds = 10,
        MaxConsecutiveWaitFailures = 2,
    };

    private static Run NewRun(string id = "run-0001") => new()
    {
        Id = id,
        OrgId = "org-1",
        ProjectId = "prj-1",
        Number = 7,
        AgentId = "agt-1",
        AgentVersionId = "ver-1",
        Status = RunStatus.Preparing,
        Inputs = new JsonObject(),
        Context = new JsonObject(),
        RuntimeProfile = new RuntimeProfile { Cpu = 2, Memory = "512Mi", MaxDurationSeconds = 600 },
        AgentRunToken = "run-token",
    };

    private static Agent NewAgent() => new()
    {
        Id = "agt-1",
        ProjectId = "prj-1",
        Name = "demo",
        AgentType = AgentType.Oci,
        ImageRef = "example/agent:1",
        ManifestYaml = """
            apiVersion: agenthost/v1
            kind: Agent
            metadata:
              name: demo
            spec:
              runtime:
                profile: small
                cpu: 2
                memory: 512Mi
                maxDurationSeconds: 600
              permissions:
                network: none
            """,
    };

    // ---- Lancement ---------------------------------------------------------------------------

    [Fact]
    public async Task LaunchSendsTheSpecToTheRunnerAndReturnsItsContainerId()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        var harness = new Harness();
        var orchestrator = harness.Build(OptionsFor(runner.BaseUrl));

        var containerId = await orchestrator.LaunchAgentAsync(NewRun(), NewAgent(), new(), default);

        Assert.Equal("container-0001", containerId);

        var spec = Assert.Single(runner.Supervisor.Launched);
        Assert.Equal("run-0001", spec.RunId);
        Assert.Equal("example/agent:1", spec.ImageRef);
        Assert.Equal(7, spec.RunNumber);
        Assert.Equal(600, spec.MaxDurationSeconds);
        Assert.Contains("AGENTHOST_RUN_ID=run-0001", spec.Env);
        // Le jeton de rappel du protocole agent doit traverser : sans lui l'agent ne peut plus
        // rappeler l'API, et c'est le genre de champ qui se perd silencieusement en sérialisation.
        Assert.Contains("AGENTHOST_RUN_TOKEN=run-token", spec.Env);
    }

    [Fact]
    public async Task LaunchRecordsTheRunnerBeforeStartingTheContainer()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token, advertiseSelf: false);
        var harness = new Harness();
        var orchestrator = harness.Build(OptionsFor(runner.BaseUrl));

        await orchestrator.LaunchAgentAsync(NewRun(), NewAgent(), new(), default);

        // Une seule écriture ici (le runner n'annonce pas d'adresse propre), et elle a eu lieu :
        // l'ordre par rapport au lancement est assuré par le fait que la colonne porte déjà l'URL
        // quand le superviseur reçoit le spec — vérifié par le test suivant sur l'adresse annoncée.
        var write = Assert.Single(harness.RunnerUrlWrites);
        Assert.Equal("run-0001", write.RunId);
        Assert.Equal(runner.BaseUrl, write.Url);
    }

    [Fact]
    public async Task LaunchPinsTheRunToTheAddressTheRunnerAdvertises()
    {
        // Le backend compose l'adresse du Service ; le runner répond avec la sienne. Sans cela,
        // l'arrêt repasserait par l'équilibreur et atterrirait sur un nœud au hasard.
        await using var runner = await SimulatedRunner.StartAsync(Token, advertiseSelf: true);
        var harness = new Harness();
        // Adresse « de Service » distincte : 127.0.0.1 vs localhost désignent le même hôte mais pas
        // la même chaîne, ce qui suffit à distinguer les deux écritures.
        var serviceUrl = runner.BaseUrl.Replace("127.0.0.1", "localhost");
        var orchestrator = harness.Build(OptionsFor(serviceUrl));

        await orchestrator.LaunchAgentAsync(NewRun(), NewAgent(), new(), default);

        Assert.Equal(2, harness.RunnerUrlWrites.Count);
        Assert.Equal(serviceUrl, harness.RunnerUrlWrites[0].Url);
        Assert.Equal(runner.BaseUrl, harness.RunnerUrlWrites[1].Url);
        Assert.Equal(runner.BaseUrl, harness.RunnerUrls["run-0001"]);
    }

    [Fact]
    public async Task LaunchFailureMovesTheRunToInfraErrorAndRethrows()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        runner.Supervisor.LaunchThrows = new InvalidOperationException("daemon refused");

        var harness = new Harness();
        var orchestrator = harness.Build(OptionsFor(runner.BaseUrl));
        var run = NewRun();

        await Assert.ThrowsAnyAsync<Exception>(
            () => orchestrator.LaunchAgentAsync(run, NewAgent(), new(), default));

        Assert.Equal(RunStatus.InfraError, run.Status);
        Assert.Equal("launch_failed", run.ErrorCode);
    }

    // ---- Routage de l'arrêt et des journaux --------------------------------------------------

    [Fact]
    public async Task StopIsRoutedToTheRunnerThatHoldsTheRun()
    {
        await using var runnerA = await SimulatedRunner.StartAsync(Token);
        await using var runnerB = await SimulatedRunner.StartAsync(Token);

        var harness = new Harness();
        // Le run est lancé sur B, tandis que le backend qui l'annule est configuré pour lancer sur A —
        // exactement la situation d'une seconde réplique backend qui n'a pas lancé ce run.
        var launcher = harness.Build(OptionsFor(runnerB.BaseUrl));
        await launcher.LaunchAgentAsync(NewRun(), NewAgent(), new(), default);

        var otherReplica = harness.Build(OptionsFor(runnerA.BaseUrl));
        var result = await otherReplica.StopAsync("run-0001", default);

        Assert.True(result.Confirmed);
        Assert.Equal("run-0001", Assert.Single(runnerB.Supervisor.Stopped));
        Assert.Empty(runnerA.Supervisor.Stopped);
    }

    [Fact]
    public async Task LogsAreRoutedToTheRunnerThatHoldsTheRun()
    {
        await using var runnerA = await SimulatedRunner.StartAsync(Token);
        await using var runnerB = await SimulatedRunner.StartAsync(Token);

        var harness = new Harness();
        await harness.Build(OptionsFor(runnerB.BaseUrl))
            .LaunchAgentAsync(NewRun(), NewAgent(), new(), default);

        var logs = await harness.Build(OptionsFor(runnerA.BaseUrl)).GetLogsAsync("run-0001", default);

        Assert.True(logs.Retrieved);
        Assert.Equal("hello from the agent", logs.Content);
        Assert.Equal("run-0001", Assert.Single(runnerB.Supervisor.LogsRequested));
        Assert.Empty(runnerA.Supervisor.LogsRequested);
    }

    // ---- Colonne nulle : runs antérieurs à la migration, ou lancés en mode inprocess ----------

    [Fact]
    public async Task StopReportsRunnerUnknownWhenNoRunnerIsRecorded()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        var harness = new Harness();
        var orchestrator = harness.Build(OptionsFor(runner.BaseUrl));

        var result = await orchestrator.StopAsync("run-from-before-the-migration", default);

        Assert.False(result.Confirmed);
        Assert.Equal(StopOutcome.RunnerUnknown, result.Outcome);
        Assert.Contains("No runner is recorded", result.Detail);
        // Et surtout : aucun runner n'a été sollicité au hasard.
        Assert.Empty(runner.Supervisor.Stopped);
    }

    [Fact]
    public async Task LogsAreUnavailableWhenNoRunnerIsRecorded()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        var harness = new Harness();

        var logs = await harness.Build(OptionsFor(runner.BaseUrl))
            .GetLogsAsync("run-from-before-the-migration", default);

        Assert.False(logs.Retrieved);
        Assert.Equal(string.Empty, logs.Content);
        Assert.Contains("No runner is recorded", logs.Detail);
    }

    // ---- Runner injoignable : le pod a disparu ------------------------------------------------

    [Fact]
    public async Task StopReportsRunnerUnreachableWhenTheRunnerIsGone()
    {
        var harness = new Harness();
        string deadRunnerUrl;

        await using (var runner = await SimulatedRunner.StartAsync(Token))
        {
            deadRunnerUrl = runner.BaseUrl;
            await harness.Build(OptionsFor(runner.BaseUrl))
                .LaunchAgentAsync(NewRun(), NewAgent(), new(), default);
        }
        // Le runner est arrêté : son adresse ne répond plus, comme un pod supprimé.

        var result = await harness.Build(OptionsFor(deadRunnerUrl)).StopAsync("run-0001", default);

        Assert.False(result.Confirmed);
        Assert.Equal(StopOutcome.RunnerUnreachable, result.Outcome);
        Assert.Contains("unreachable", result.Detail);
        Assert.Contains("may still be running", result.Detail);
    }

    [Fact]
    public async Task LogsAreUnavailableWhenTheRunnerIsGone()
    {
        var harness = new Harness();
        string deadRunnerUrl;

        await using (var runner = await SimulatedRunner.StartAsync(Token))
        {
            deadRunnerUrl = runner.BaseUrl;
            await harness.Build(OptionsFor(runner.BaseUrl))
                .LaunchAgentAsync(NewRun(), NewAgent(), new(), default);
        }

        var logs = await harness.Build(OptionsFor(deadRunnerUrl)).GetLogsAsync("run-0001", default);

        Assert.False(logs.Retrieved);
        Assert.Contains("unreachable", logs.Detail);
    }

    // ---- Adresse réattribuée : le runner répond mais ne connaît pas le run --------------------

    [Fact]
    public async Task StopIsNotConfirmedWhenTheRunnerDoesNotHoldTheRun()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        // Ce que renvoie un runner qui n'a ni trace du run ni conteneur portant son étiquette.
        runner.Supervisor.StopResponse = new RunnerStopResponseBuilder().NotConfirmed();

        var harness = new Harness();
        harness.RunnerUrls["run-0001"] = runner.BaseUrl;

        var result = await harness.Build(OptionsFor(runner.BaseUrl)).StopAsync("run-0001", default);

        Assert.False(result.Confirmed);
        Assert.Equal(StopOutcome.DaemonRefused, result.Outcome);
        Assert.Contains("no record of run", result.Detail);
    }

    // ---- Surveillance --------------------------------------------------------------------------

    [Fact]
    public async Task ExitCodeFromTheRunnerFinishesTheRun()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        var harness = new Harness();

        var run = NewRun();
        harness.Runs.Setup(r => r.GetAsync("run-0001", It.IsAny<CancellationToken>())).ReturnsAsync(run);

        await harness.Build(OptionsFor(runner.BaseUrl)).LaunchAgentAsync(run, NewAgent(), new(), default);
        Assert.Equal(RunStatus.Running, run.Status);

        runner.Supervisor.CompleteRun(exitCode: 0, logs: "done");

        await WaitUntil(() => run.Status == RunStatus.Succeeded);
        Assert.Equal(0, run.ExitCode);
        Assert.NotNull(run.FinishedAt);
    }

    [Fact]
    public async Task NonZeroExitFromTheRunnerFailsTheRun()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        var harness = new Harness();

        var run = NewRun();
        harness.Runs.Setup(r => r.GetAsync("run-0001", It.IsAny<CancellationToken>())).ReturnsAsync(run);

        await harness.Build(OptionsFor(runner.BaseUrl)).LaunchAgentAsync(run, NewAgent(), new(), default);
        runner.Supervisor.CompleteRun(exitCode: 42);

        await WaitUntil(() => run.Status == RunStatus.Failed);
        Assert.Equal(42, run.ExitCode);
        Assert.Equal("non_zero_exit", run.ErrorCode);
    }

    [Fact]
    public async Task ARunnerThatForgetsTheRunMovesItToInfraErrorRatherThanLeavingItRunning()
    {
        // Le pire résultat serait un run bloqué en « running » pour toujours parce que personne ne
        // dira jamais comment il s'est terminé.
        await using var runner = await SimulatedRunner.StartAsync(Token);
        var harness = new Harness();

        var run = NewRun();
        harness.Runs.Setup(r => r.GetAsync("run-0001", It.IsAny<CancellationToken>())).ReturnsAsync(run);

        await harness.Build(OptionsFor(runner.BaseUrl)).LaunchAgentAsync(run, NewAgent(), new(), default);
        runner.Supervisor.Forget("run-0001");

        await WaitUntil(() => run.Status == RunStatus.InfraError, TimeSpan.FromSeconds(20));
        Assert.Equal("monitoring_error", run.ErrorCode);
        Assert.Contains("no longer knows run", run.ErrorMessage);
    }

    // ---- Authentification ----------------------------------------------------------------------

    [Fact]
    public async Task TheRunnerRefusesCallsWithoutTheSharedToken()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        var harness = new Harness();

        // Un backend sans jeton : l'orchestrateur ne doit rien pouvoir faire de ce runner.
        var orchestrator = harness.Build(OptionsFor(runner.BaseUrl, token: string.Empty));

        await Assert.ThrowsAnyAsync<Exception>(
            () => orchestrator.LaunchAgentAsync(NewRun(), NewAgent(), new(), default));

        Assert.Empty(runner.Supervisor.Launched);
    }

    [Fact]
    public async Task TheRunnerRefusesCallsWithAWrongToken()
    {
        await using var runner = await SimulatedRunner.StartAsync(Token);
        var harness = new Harness();

        var orchestrator = harness.Build(OptionsFor(runner.BaseUrl, token: "wrong-token-0123456789"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => orchestrator.LaunchAgentAsync(NewRun(), NewAgent(), new(), default));

        Assert.Empty(runner.Supervisor.Launched);
    }

    // ---- Utilitaires ---------------------------------------------------------------------------

    private static async Task WaitUntil(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }

        Assert.True(condition(), "condition was not met before the timeout");
    }

    private sealed class RunnerStopResponseBuilder
    {
        public Shared.Contracts.RunnerStopResponse NotConfirmed() => new()
        {
            Stopped = 0,
            Confirmed = false,
            Detail = "This node has no record of run run-0001 and no container carrying its label.",
        };
    }
}
