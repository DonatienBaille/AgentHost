using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
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
/// Container-based agent orchestrator (spec section 7.1). Drives any engine that speaks the Docker
/// REST API — Docker itself and Podman's compat service; the endpoint is resolved by
/// <see cref="Infrastructure.ContainerRuntimeEndpoint"/> and no code path branches on which one it
/// is. Bind-mount sources are translated to the daemon's view of the filesystem by
/// <see cref="Infrastructure.ContainerPathMapper"/>, which is what makes the backend runnable
/// inside a container. Applies the section 13 threat-model
/// mitigations directly on the container's HostConfig: no-new-privileges, full capability
/// drop (+ NET_BIND_SERVICE only when the manifest allows outbound network), strict memory
/// with no swap, a read-only rootfs (writable /workspace bind + /tmp tmpfs), a read-only
/// secrets bind mount that is destroyed as soon as the container exits, configurable DNS, and
/// a stop timeout derived from the run's max duration.
///
/// Secrets are delivered to the container ONLY as files under <c>/run/secrets/&lt;NAME&gt;</c>.
/// They are deliberately not exported as <c>SECRET_*</c> environment variables: container env is
/// readable by anyone who can call <c>docker inspect</c> and by every process in the container via
/// <c>/proc/1/environ</c>, which would defeat the read-only secrets mount. See
/// <c>docs/agent-protocol.md</c> section 1.
///
/// Network policy (<c>spec.permissions.network</c>):
/// <list type="bullet">
///   <item><c>none</c> — <c>NetworkMode=none</c>, no interface at all.</item>
///   <item><c>full</c> — <c>NetworkMode=bridge</c>, unrestricted egress.</item>
///   <item><c>allowlist</c> — bridge (or <c>Docker:AllowlistNetwork</c> when configured) plus
///   <c>HTTP_PROXY</c>/<c>HTTPS_PROXY</c>/<c>NO_PROXY</c> pointing at <c>Docker:EgressProxy</c>;
///   the manifest's <c>permissions.networkAllowlist</c> hosts are handed to the proxy tier via the
///   <c>agenthost.network_allowlist</c> container label and the informational
///   <c>AGENTHOST_NETWORK_ALLOWLIST</c> env var. With no proxy configured the run fails CLOSED
///   (no network) rather than silently degrading to full egress.</item>
/// </list>
/// RESIDUAL LIMITATION: proxy-based egress control is only binding for proxy-aware clients. A
/// process that opens a raw socket to an IP ignores <c>HTTP_PROXY</c>. Real enforcement requires
/// that the container's network cannot route anywhere except the proxy — deploy the allowlist runs
/// on an internal Docker network (<c>Docker:AllowlistNetwork</c>, e.g. a network created with
/// <c>--internal</c> where only the proxy is reachable), or firewall the bridge subnet upstream.
/// The orchestrator cannot do that per-container by itself without a per-run proxy sidecar.
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
    private readonly ContainerPathMapper _paths;
    private readonly string[] _dns;
    private readonly string? _containerRuntime;
    private readonly string? _egressProxy;
    private readonly string _noProxy;
    private readonly string? _allowlistNetwork;
    private readonly bool _allowUnconfinedAllowlist;
    private readonly int _tmpfsSizeMb;
    private readonly string[] _securityOpt;

    public ContainerOrchestrator(
        DockerClient docker,
        IRunRepository runRepository,
        IEventBus eventBus,
        IAgentManifestParser manifestParser,
        IServiceScopeFactory scopeFactory,
        RunStateMachine stateMachine,
        ILogger logger,
        IConfiguration config,
        ContainerPathMapper paths)
    {
        _docker = docker;
        _runRepository = runRepository;
        _eventBus = eventBus;
        _manifestParser = manifestParser;
        _scopeFactory = scopeFactory;
        _stateMachine = stateMachine;
        _logger = logger;
        _paths = paths;

        // Empty by default: containers inherit the Docker daemon's own resolver configuration.
        // A sovereign/on-prem deployment sets Docker:Dns:0/1/... to its internal resolvers; no
        // public third-party resolver is ever used implicitly.
        _dns = config.GetSection("Docker:Dns").Get<string[]>() ?? Array.Empty<string>();
        _containerRuntime = NullIfBlank(config["Docker:Runtime"]);
        _egressProxy = config["Docker:EgressProxy"];
        _noProxy = config["Docker:NoProxy"] ?? "localhost,127.0.0.1,::1";
        _allowlistNetwork = NullIfBlank(config["Docker:AllowlistNetwork"]);
        _allowUnconfinedAllowlist = bool.TryParse(config["Docker:AllowUnconfinedAllowlist"], out var unconfined) && unconfined;
        _tmpfsSizeMb = int.TryParse(config["Docker:TmpfsSizeMb"], out var mb) && mb > 0 ? mb : 64;
        _securityOpt = ResolveSecurityOpt(config);
    }

    /// <summary>
    /// Container security options. Configurable (<c>Docker:SecurityOpt:0/1/...</c>) only because the
    /// accepted spelling of <c>no-new-privileges</c> has historically varied between engines and
    /// versions: Docker's daemon accepts the bare key, <c>=true</c> and <c>:true</c>; Podman's compat
    /// handler splits on <c>=</c> first and also special-cases the bare key. <c>=true</c> is the form
    /// both parse today and stays the default; an operator hitting an "invalid --security-opt" from an
    /// older Podman can set the bare <c>no-new-privileges</c> without a code change.
    /// </summary>
    private static string[] ResolveSecurityOpt(IConfiguration config)
    {
        var configured = config.GetSection("Docker:SecurityOpt").Get<string[]>();
        return configured is { Length: > 0 } ? configured : new[] { "no-new-privileges=true" };
    }

    /// <summary>
    /// Converts the manifest's <c>runtime.cpu</c> (in cores) to the daemon's NanoCPUs unit.
    /// A non-positive value means "unlimited", which is what leaving the field at 0 expresses.
    /// </summary>
    internal static long NanoCpus(long cpuCores) => cpuCores > 0 ? cpuCores * 1_000_000_000L : 0L;

    public async Task<string> LaunchAgentAsync(
        Run run,
        Agent agent,
        Dictionary<string, string> secrets,
        CancellationToken ct)
    {
        // Every log line emitted while launching/monitoring this run carries RunId (spec 14.1),
        // including those from nested calls that don't take the run as a parameter.
        using var runIdProperty = Serilog.Context.LogContext.PushProperty("RunId", run.Id);

        try
        {
            var manifest = _manifestParser.Parse(agent.ManifestYaml);
            var containerPolicy = _manifestParser.ParseContainerPolicy(agent.ManifestYaml);

            var envVars = new List<string>
            {
                $"AGENTHOST_RUN_ID={run.Id}",
                $"AGENTHOST_PROJECT_ID={run.ProjectId}",
                $"AGENTHOST_INPUTS={JsonSerializer.Serialize(run.Inputs)}",
                "AGENTHOST_PROTOCOL_VERSION=1.0",
                // Run-scoped callback credential for the agent protocol (docs/agent-protocol.md);
                // minted in RunService just before launch, never persisted.
                $"AGENTHOST_RUN_TOKEN={run.AgentRunToken}",
            };

            // NOTE: secrets are intentionally NOT added to envVars — they are delivered only as
            // files under /run/secrets (see the class remarks and docs/agent-protocol.md).

            if (agent.Type != AgentType.Oci && manifest.Spec.External?.Config is { } externalConfig)
            {
                foreach (var (key, value) in externalConfig)
                    envVars.Add($"AGENT_{key.ToUpperInvariant()}={value}");
            }

            // Local paths: what THIS process reads and writes. The daemon's view of the same two
            // directories is computed at bind-spec time (ContainerPathMapper) and may differ.
            var workspaceDir = _paths.LocalWorkspaceDirectory(run.Id);
            var secretsDir = SecretsDirectory(run.Id);

            Directory.CreateDirectory(workspaceDir);
            Directory.CreateDirectory(secretsDir);
            // 0700 on the directory, 0400 on each file. File.SetAttributes(ReadOnly) — what this
            // used to do — clears the write bits at best and is a no-op for confidentiality; the
            // Unix file mode is what actually keeps other local accounts out of the plaintext.
            // (Inside the container the bind mount is namespaced; a container running as uid 0
            // still reads the file, which is the intended delivery path.)
            SetUnixMode(secretsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            foreach (var (key, value) in secrets)
            {
                var secretPath = Path.Combine(secretsDir, key);
                await File.WriteAllTextAsync(secretPath, value, ct);
                SetUnixMode(secretPath, UnixFileMode.UserRead);
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

            var network = ResolveNetworkPolicy(manifest.Spec.Permissions.Network, containerPolicy, run.Id);
            var capAdd = network.HasNetwork ? new[] { "NET_BIND_SERVICE" } : Array.Empty<string>();
            envVars.AddRange(network.EnvVars);

            var labels = new Dictionary<string, string>
            {
                { "agenthost.run_id", run.Id },
                { "agenthost.project_id", run.ProjectId },
                { "agenthost.agent_id", run.AgentId },
                { "agenthost.network", network.Mode },
            };
            if (network.Allowlist.Count > 0)
                labels["agenthost.network_allowlist"] = string.Join(",", network.Allowlist);

            if (containerPolicy.WritableRootfs)
            {
                _logger.Warning(
                    "Run {RunId} uses agent {AgentId} whose manifest opts into a writable root filesystem " +
                    "(spec.permissions.writableRootfs: true); the container is less isolated than the default",
                    run.Id, run.AgentId);
            }

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
                        // Resources.
                        //
                        // NanoCPUs, not CPUCount: CPUCount is a Windows-container field that both
                        // the Linux Docker daemon and Podman ignore outright, so the manifest's
                        // runtime.cpu used to constrain exactly nothing. NanoCPUs is the portable
                        // Linux mechanism (cores x 1e9; the daemon turns it into cpu.max /
                        // CpuQuota+CpuPeriod) and Podman's compat API maps it the same way.
                        NanoCPUs = NanoCpus(run.RuntimeProfile.Cpu),
                        Memory = run.RuntimeProfile.MemoryBytes,
                        MemorySwap = run.RuntimeProfile.MemoryBytes, // no swap

                        // Security (spec section 13.2)
                        SecurityOpt = _securityOpt,

                        // Isolated runtime (gVisor "runsc", Kata "kata-runtime", "sysbox-runc").
                        // Empty = the daemon's default (runc), which shares the host kernel: a
                        // container escape is a host compromise. For a platform whose business is
                        // running third-party code this is the structural mitigation, and it costs
                        // one field — but the runtime must be installed and registered with the
                        // daemon first, so it cannot be the default here.
                        Runtime = _containerRuntime,
                        // Upper-case "ALL": both engines normalize capability names, but Podman's
                        // compat path compares the drop-everything sentinel case-sensitively in
                        // places, and "ALL" is the spelling both accept.
                        CapDrop = new[] { "ALL" },
                        CapAdd = capAdd,

                        // Read-only rootfs by default: the agent writes to the /workspace bind and
                        // to the /tmp tmpfs, nothing else. Manifests that genuinely need a writable
                        // rootfs must opt in explicitly (spec.permissions.writableRootfs: true).
                        ReadonlyRootfs = !containerPolicy.WritableRootfs,

                        // Volumes. Sources are the DAEMON's view of these directories, not this
                        // process's — see ContainerPathMapper.
                        Binds = new[]
                        {
                            _paths.BindSpec(workspaceDir, "/workspace"),
                            _paths.BindSpec(secretsDir, "/run/secrets", "ro"),
                        },
                        Tmpfs = new Dictionary<string, string>
                        {
                            { "/tmp", $"rw,nosuid,nodev,size={_tmpfsSizeMb}m" },
                        },

                        // Networking
                        NetworkMode = network.Mode,
                        // Omitted (empty) unless Docker:Dns is configured, so containers inherit
                        // the daemon's resolver instead of a hardcoded public one.
                        DNS = _dns,
                        DNSSearch = Array.Empty<string>(),
                    },

                    Labels = labels,
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

            // The container never started (or never got far enough to be monitored), so nothing
            // else will clean up the plaintext secrets we may already have written.
            DeleteRunSecrets(run.Id);

            run.ErrorCode = "launch_failed";
            run.ErrorMessage = ex.Message;
            // InfraError (not Failed) because this can happen while run.Status is still
            // Preparing, and Failed is only a valid transition from Finalizing (spec 8.2).
            await _stateMachine.TransitionAsync(run, RunStatus.InfraError, "launch failed", ct);

            throw;
        }
    }

    /// <summary>
    /// Maps <c>spec.permissions.network</c> onto a Docker network mode plus the proxy environment
    /// the container needs. The decision itself lives in <see cref="NetworkPolicyResolver"/> — it is
    /// policy, not Docker plumbing, and keeping it separate is what makes it directly testable.
    /// </summary>
    private NetworkPolicy ResolveNetworkPolicy(string? requested, AgentContainerPolicy policy, string runId) =>
        NetworkPolicyResolver.Resolve(
            requested, policy.NetworkAllowlist, runId,
            new NetworkPolicyResolver.Settings(_egressProxy, _allowlistNetwork, _noProxy, _allowUnconfinedAllowlist),
            _logger);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();


    private string SecretsDirectory(string runId) => _paths.LocalSecretsDirectory(runId);

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return; // File.SetUnixFileMode throws PlatformNotSupportedException on Windows.

        File.SetUnixFileMode(path, mode);
    }

    /// <summary>
    /// Removes the run's plaintext secrets from disk. Called as soon as the container exits (and on
    /// a failed launch): the secrets are only needed for the lifetime of the container's bind mount,
    /// and anything longer is unencrypted key material sitting in the runs directory.
    /// </summary>
    private void DeleteRunSecrets(string runId)
    {
        var dir = SecretsDirectory(runId);
        try
        {
            if (!Directory.Exists(dir))
                return;

            Directory.Delete(dir, recursive: true);
            _logger.Debug("Deleted secrets directory for run {RunId}", runId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete secrets directory {Directory} for run {RunId}; " +
                              "plaintext secrets may remain on disk", dir, runId);
        }
    }

    private async Task MonitorContainerAsync(string containerId, string runId, DateTime startedAt)
    {
        using var runIdProperty = Serilog.Context.LogContext.PushProperty("RunId", runId);
        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RunStateMachine>();

        try
        {
            var exitCode = await _docker.Containers.WaitContainerAsync(containerId, CancellationToken.None);

            _logger.Information("Container {ContainerId} exited with code {ExitCode}",
                containerId, exitCode.StatusCode);

            // First thing after the container is gone: shred the plaintext secrets. Everything
            // below (log collection, state transitions) can fail without leaving them behind.
            DeleteRunSecrets(runId);

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
        finally
        {
            // Backstop for every path that doesn't reach the post-exit deletion above (wait failed,
            // process crash between the two, ...). Idempotent.
            DeleteRunSecrets(runId);
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
