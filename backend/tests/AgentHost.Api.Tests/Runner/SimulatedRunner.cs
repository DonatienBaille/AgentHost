using AgentHost.Runner;
using AgentHost.Shared.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace AgentHost.Api.Tests.Runner;

/// <summary>
/// A runner tier hosted on a real Kestrel socket, serving the <b>real</b>
/// <see cref="RunnerEndpoints"/> — the same bearer-token filter, the same status codes, the same
/// JSON shapes — in front of a fake <see cref="IRunSupervisor"/>.
///
/// <para>What is simulated is exactly and only the container daemon. There is none in this
/// environment (<c>/var/run/docker.sock</c> does not exist), so no test here proves that a
/// container starts. What these tests do prove is the part the runner tier actually added: the
/// wire protocol between the two processes, and the routing of stop/logs to the runner that holds
/// a given run.</para>
/// </summary>
public sealed class SimulatedRunner : IAsyncDisposable
{
    private readonly IHost _host;

    public FakeRunSupervisor Supervisor { get; }

    /// <summary>Base URL a backend dials, e.g. <c>http://127.0.0.1:41234</c>.</summary>
    public string BaseUrl { get; }

    private SimulatedRunner(IHost host, FakeRunSupervisor supervisor, string baseUrl)
    {
        _host = host;
        Supervisor = supervisor;
        BaseUrl = baseUrl;
    }

    /// <param name="authToken">Shared bearer token the runner will require.</param>
    /// <param name="advertiseSelf">
    /// When true the runner answers launches with its own address, the way the DaemonSet does
    /// through <c>Runner:AdvertisedUrl</c>. When false it advertises nothing, which is the
    /// single-runner (docker-compose) case.
    /// </param>
    public static async Task<SimulatedRunner> StartAsync(string authToken, bool advertiseSelf = true)
    {
        var supervisor = new FakeRunSupervisor();

        // Un port libre réservé à l'avance plutôt que le port éphémère 0 : l'adresse doit être
        // connue AVANT le mappage des routes, puisque le runner l'annonce dans sa réponse de
        // lancement et que le mappage doit précéder le démarrage.
        var address = $"http://127.0.0.1:{FreePort()}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(address);
        // Silence par configuration plutôt que par builder.Logging.SetMinimumLevel : importer
        // Microsoft.Extensions.Logging ici rendrait toute référence nue à `ILogger` ambiguë avec
        // celle de Serilog, qui est la seule utilisée dans ce dépôt.
        ((IConfigurationBuilder)builder.Configuration).AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Logging:LogLevel:Default"] = "None",
        });
        builder.Services.AddSingleton<ILogger>(Serilog.Core.Logger.None);
        builder.Services.AddSingleton<IRunSupervisor>(supervisor);

        var app = builder.Build();

        // Les routes DOIVENT être mappées avant StartAsync : WebApplication fige son pipeline au
        // démarrage, et un MapPost posé après ne rejoint jamais la table de routage — la requête
        // repart en 404, sans le moindre message expliquant pourquoi.
        app.MapRunnerEndpoints(authToken, advertiseSelf ? address : null);

        await app.StartAsync();
        return new SimulatedRunner(app, supervisor, address);
    }

    /// <summary>
    /// Un port TCP que le noyau vient d'attribuer et que l'on relâche aussitôt. La fenêtre de course
    /// est théorique dans une suite de tests, et c'est le prix à payer pour connaître l'adresse
    /// avant le démarrage.
    /// </summary>
    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}

/// <summary>
/// A supervisor that records what it was asked and answers what the test tells it to. It stands in
/// for the container daemon, nothing more.
/// </summary>
public sealed class FakeRunSupervisor : IRunSupervisor
{
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource<RunnerOutcome> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public List<AgentLaunchSpec> Launched { get; } = new();
    public List<string> Stopped { get; } = new();
    public List<string> LogsRequested { get; } = new();

    public string ContainerId { get; set; } = "container-0001";
    public Exception? LaunchThrows { get; set; }
    public RunnerStopResponse StopResponse { get; set; } = new() { Stopped = 1, Confirmed = true };
    public RunnerLogsResponse LogsResponse { get; set; } = new() { Logs = "hello from the agent", Retrieved = true };

    public Task<string> LaunchAsync(AgentLaunchSpec spec, CancellationToken ct)
    {
        if (LaunchThrows is not null)
            return Task.FromException<string>(LaunchThrows);

        Launched.Add(spec);
        lock (_known) _known.Add(spec.RunId);
        return Task.FromResult(ContainerId);
    }

    public async Task<RunnerOutcome?> WaitAsync(string runId, TimeSpan timeout, CancellationToken ct)
    {
        if (!Knows(runId))
            return null;

        var finished = await Task.WhenAny(_outcome.Task, Task.Delay(timeout, ct));
        return finished == _outcome.Task
            ? await _outcome.Task
            : new RunnerOutcome { Exited = false };
    }

    public Task<RunnerStopResponse> StopAsync(string runId, CancellationToken ct)
    {
        Stopped.Add(runId);
        return Task.FromResult(StopResponse);
    }

    public Task<RunnerLogsResponse> GetLogsAsync(string runId, CancellationToken ct)
    {
        LogsRequested.Add(runId);
        return Task.FromResult(LogsResponse);
    }

    public bool Knows(string runId)
    {
        lock (_known) return _known.Contains(runId);
    }

    /// <summary>Makes the pending long poll return, as a container exit would.</summary>
    public void CompleteRun(long exitCode, string logs = "") =>
        _outcome.TrySetResult(new RunnerOutcome
        {
            Exited = true,
            ExitCode = exitCode,
            Logs = logs,
            FinishedAt = DateTime.UtcNow,
        });

    /// <summary>Pretends this node never heard of the run — a recycled pod address.</summary>
    public void Forget(string runId)
    {
        lock (_known) _known.Remove(runId);
    }
}
