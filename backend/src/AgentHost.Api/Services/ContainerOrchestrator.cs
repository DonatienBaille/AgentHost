using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AgentHost.Api.Services;

public interface IContainerOrchestrator
{
    Task<string> LaunchAgentAsync(Run run, Agent agent, Dictionary<string, string> secrets, CancellationToken ct);
    Task StopAsync(string runId, CancellationToken ct);
    Task<string> GetLogsAsync(string runId, CancellationToken ct);
}

/// <summary>
/// Docker-based agent orchestrator (spec section 7.1). Applies the section 13 threat-model
/// mitigations directly on the container's HostConfig: no-new-privileges, full capability
/// drop (+ NET_BIND_SERVICE only when the manifest allows outbound network), strict memory
/// with no swap, a read-only secrets bind mount, a single DNS resolver, and a stop timeout
/// derived from the run's max duration.
/// </summary>
public class ContainerOrchestrator : IContainerOrchestrator
{
    private readonly DockerClient _docker;
    private readonly IRunRepository _runRepository;
    private readonly IEventBus _eventBus;
    private readonly IAgentManifestParser _manifestParser;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RunStateMachine _stateMachine;
    private readonly ILogger _logger;
    private readonly string _workspacePath;

    public ContainerOrchestrator(
        DockerClient docker,
        IRunRepository runRepository,
        IEventBus eventBus,
        IAgentManifestParser manifestParser,
        IServiceScopeFactory scopeFactory,
        RunStateMachine stateMachine,
        ILogger logger,
        IConfiguration config)
    {
        _docker = docker;
        _runRepository = runRepository;
        _eventBus = eventBus;
        _manifestParser = manifestParser;
        _scopeFactory = scopeFactory;
        _stateMachine = stateMachine;
        _logger = logger;
        _workspacePath = config["Docker:WorkspacePath"] ?? "/var/agenthost/runs";
    }

    public async Task<string> LaunchAgentAsync(
        Run run,
        Agent agent,
        Dictionary<string, string> secrets,
        CancellationToken ct)
    {
        try
        {
            var manifest = _manifestParser.Parse(agent.ManifestYaml);

            var envVars = new List<string>
            {
                $"AGENTHOST_RUN_ID={run.Id}",
                $"AGENTHOST_PROJECT_ID={run.ProjectId}",
                $"AGENTHOST_INPUTS={JsonSerializer.Serialize(run.Inputs)}",
                "AGENTHOST_PROTOCOL_VERSION=1.0",
            };

            foreach (var (key, value) in secrets)
                envVars.Add($"SECRET_{key}={value}");

            if (agent.Type != AgentType.Oci && manifest.Spec.External?.Config is { } externalConfig)
            {
                foreach (var (key, value) in externalConfig)
                    envVars.Add($"AGENT_{key.ToUpperInvariant()}={value}");
            }

            var workspaceDir = Path.Combine(_workspacePath, run.Id, "workspace");
            var secretsDir = Path.Combine(_workspacePath, run.Id, "secrets");

            Directory.CreateDirectory(workspaceDir);
            Directory.CreateDirectory(secretsDir);

            foreach (var (key, value) in secrets)
            {
                var secretPath = Path.Combine(secretsDir, key);
                await File.WriteAllTextAsync(secretPath, value, ct);
                File.SetAttributes(secretPath, FileAttributes.ReadOnly);
            }

            var imageRef = agent.ImageRef ?? $"agenthost/{agent.Type.ToDbString()}:latest";

            _logger.Information("Pulling image {ImageRef} for run {RunId}", imageRef, run.Id);

            try
            {
                var progress = new Progress<JSONMessage>(msg =>
                {
                    if (!string.IsNullOrEmpty(msg.Status))
                        _logger.Debug("Docker: {Status}", msg.Status);
                });

                await _docker.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = imageRef },
                    null,
                    progress,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to pull image {ImageRef}, using cached", imageRef);
            }

            var networkAllowed = manifest.Spec.Permissions.Network is "allowlist" or "full";
            var capAdd = networkAllowed ? new[] { "NET_BIND_SERVICE" } : Array.Empty<string>();

            var containerResponse = await _docker.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    Image = imageRef,
                    Name = $"agenthost-run-{run.Id}",
                    Hostname = $"run-{run.Number}",
                    Env = envVars,

                    // Give the container maxDuration + 30s grace before Docker force-kills it
                    // (spec 13.2). Applies to `docker stop`, not the run's own state machine.
                    StopTimeout = TimeSpan.FromSeconds(run.RuntimeProfile.MaxDurationSeconds + 30),

                    HostConfig = new HostConfig
                    {
                        // Resources
                        CPUCount = run.RuntimeProfile.Cpu,
                        Memory = run.RuntimeProfile.MemoryBytes,
                        MemorySwap = run.RuntimeProfile.MemoryBytes, // no swap

                        // Security (spec section 13.2)
                        SecurityOpt = new[] { "no-new-privileges=true" },
                        CapDrop = new[] { "all" },
                        CapAdd = capAdd,
                        ReadonlyRootfs = false,

                        // Volumes
                        Binds = new[]
                        {
                            $"{workspaceDir}:/workspace",
                            $"{secretsDir}:/run/secrets:ro",
                        },

                        // Networking
                        NetworkMode = networkAllowed ? "bridge" : "none",
                        DNS = new[] { "8.8.8.8" },
                        DNSSearch = Array.Empty<string>(),
                    },

                    Labels = new Dictionary<string, string>
                    {
                        { "agenthost.run_id", run.Id },
                        { "agenthost.project_id", run.ProjectId },
                        { "agenthost.agent_id", run.AgentId },
                    },
                },
                ct);

            await _docker.Containers.StartContainerAsync(
                containerResponse.ID,
                new ContainerStartParameters(),
                ct);

            _logger.Information("Container {ContainerId} started for run {RunId}",
                containerResponse.ID, run.Id);

            run.StartedAt = DateTime.UtcNow;
            await _stateMachine.TransitionAsync(run, RunStatus.Running, "container started", ct);

            // Monitor in the background using a fresh DI scope: the caller's HTTP request
            // scope (and its scoped repositories/DbConnections) may be disposed long before
            // the container finishes, since runs can outlive a single request by hours.
            _ = MonitorContainerAsync(containerResponse.ID, run.Id, run.StartedAt.Value);

            return containerResponse.ID;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to launch agent for run {RunId}", run.Id);

            run.ErrorCode = "launch_failed";
            run.ErrorMessage = ex.Message;
            // InfraError (not Failed) because this can happen while run.Status is still
            // Preparing, and Failed is only a valid transition from Finalizing (spec 8.2).
            await _stateMachine.TransitionAsync(run, RunStatus.InfraError, "launch failed", ct);

            throw;
        }
    }

    private async Task MonitorContainerAsync(string containerId, string runId, DateTime startedAt)
    {
        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RunStateMachine>();

        try
        {
            var exitCode = await _docker.Containers.WaitContainerAsync(containerId, CancellationToken.None);

            _logger.Information("Container {ContainerId} exited with code {ExitCode}",
                containerId, exitCode.StatusCode);

            string logContent;
            try
            {
                // Containers run without a TTY, so stdout/stderr come back multiplexed
                // (interleaved with 8-byte frame headers) — the `tty: false` overload demuxes
                // them for us into a clean (stdout, stderr) pair.
                using var logs = await _docker.Containers.GetContainerLogsAsync(
                    containerId,
                    false,
                    new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = false },
                    CancellationToken.None);

                var (stdout, stderr) = await logs.ReadOutputToEndAsync(CancellationToken.None);
                logContent = stdout + stderr;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to collect logs for container {ContainerId}", containerId);
                logContent = string.Empty;
            }

            try
            {
                await _docker.Containers.RemoveContainerAsync(
                    containerId,
                    new ContainerRemoveParameters { Force = true },
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to remove container {ContainerId}", containerId);
            }

            var run = await runRepository.GetAsync(runId, CancellationToken.None);
            if (run is null)
            {
                _logger.Warning("Run {RunId} not found while finalizing container {ContainerId}", runId, containerId);
                return;
            }

            run.ExitCode = (int)exitCode.StatusCode;
            run.FinishedAt = DateTime.UtcNow;
            run.DurationMs = (long)(run.FinishedAt.Value - startedAt).TotalMilliseconds;
            if (exitCode.StatusCode != 0)
            {
                run.ErrorCode = "non_zero_exit";
                run.ErrorMessage = $"Container exited with code {exitCode.StatusCode}";
            }

            // Succeeded/Failed are only valid transitions from Finalizing (spec 8.2).
            await stateMachine.TransitionAsync(run, RunStatus.Finalizing, "container exited", CancellationToken.None);
            await stateMachine.TransitionAsync(
                run, exitCode.StatusCode == 0 ? RunStatus.Succeeded : RunStatus.Failed, "container exited", CancellationToken.None);

            await eventBus.PublishAsync(new RunEvent
            {
                RunId = runId,
                EventType = "run.finished",
                Level = "info",
                Message = $"Run finished with exit code {exitCode.StatusCode}",
                Payload = new { exitCode = exitCode.StatusCode, duration = run.DurationMs, logLength = logContent.Length },
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error monitoring container {ContainerId}", containerId);

            var run = await runRepository.GetAsync(runId, CancellationToken.None);
            if (run is not null && !run.Status.IsTerminal())
            {
                run.ErrorCode = "monitoring_error";
                run.ErrorMessage = ex.Message;
                await stateMachine.TransitionAsync(run, RunStatus.InfraError, "monitoring error", CancellationToken.None);
            }
        }
    }

    public async Task StopAsync(string runId, CancellationToken ct)
    {
        try
        {
            var containers = await _docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        { "label", new Dictionary<string, bool> { { $"agenthost.run_id={runId}", true } } },
                    },
                },
                ct);

            foreach (var container in containers)
            {
                await _docker.Containers.StopContainerAsync(
                    container.ID,
                    new ContainerStopParameters { WaitBeforeKillSeconds = 10 },
                    ct);

                _logger.Information("Container {ContainerId} stopped for run {RunId}",
                    container.ID, runId);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error stopping container for run {RunId}", runId);
        }
    }

    public async Task<string> GetLogsAsync(string runId, CancellationToken ct)
    {
        try
        {
            var containers = await _docker.Containers.ListContainersAsync(
                new ContainersListParameters
                {
                    Filters = new Dictionary<string, IDictionary<string, bool>>
                    {
                        { "label", new Dictionary<string, bool> { { $"agenthost.run_id={runId}", true } } },
                    },
                    All = true,
                },
                ct);

            if (!containers.Any())
                return $"No container found for run {runId}";

            var containerId = containers.First().ID;
            using var logsStream = await _docker.Containers.GetContainerLogsAsync(
                containerId,
                false,
                new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = false },
                ct);

            var (stdout, stderr) = await logsStream.ReadOutputToEndAsync(ct);
            return stdout + stderr;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error getting logs for run {RunId}", runId);
            return $"Error retrieving logs: {ex.Message}";
        }
    }
}
