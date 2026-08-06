using System.Formats.Tar;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// The one test in this repository that actually launches a container.
///
/// <para>Everything else that touches the run lifecycle simulates the container: it writes the run
/// state a container <em>would</em> have produced. That leaves the product's core path — image
/// resolution, container creation with the section 13 hardening flags, secret delivery through the
/// read-only <c>/run/secrets</c> mount, an agent authenticating back with
/// <c>AGENTHOST_RUN_TOKEN</c> from inside its network namespace, exit handling, log collection,
/// container removal and plaintext-secret shredding — executed nowhere. This fixture executes it,
/// once, against a real daemon.</para>
///
/// <para><b>How the container reaches the API.</b> Under <c>TestServer</c> there is no socket to
/// dial, so the app is additionally hosted on Kestrel bound to <c>0.0.0.0</c> on an ephemeral port
/// (<see cref="KestrelAgentHostApiFactory"/>). The container dials the docker bridge's gateway
/// address on that port — the gateway is read from the daemon (<c>/networks/bridge</c>), never
/// hardcoded, and <c>host.docker.internal</c> is deliberately not used because it does not exist on
/// Linux daemons.</para>
///
/// <para><b>How the agent's behaviour gets into the container.</b> The orchestrator never sets a
/// container command — an agent image carries its own entrypoint — so the behaviour is supplied as
/// an image, built at test time from <c>alpine</c> plus a shell script through the daemon's own
/// build endpoint. No registry, no push, no committed fixture image. The tag is prefixed
/// <c>localhost:5000/</c> so that the orchestrator's pull attempt (which always runs, and always
/// fails for a local-only tag) fails instantly against a refused connection instead of doing a
/// round trip to Docker Hub.</para>
///
/// <para><b>Determinism.</b> The container's assertions all run immediately, but it then waits for
/// the host to create a release file inside the <c>/workspace</c> bind mount before exiting. That
/// removes the race that would otherwise decide this test: the orchestrator removes the container
/// within milliseconds of its exit, so a short-lived container could be gone before the host could
/// inspect the hardening flags it was created with.</para>
///
/// <para>Skipped — never silently passed — when no daemon answers; see
/// <see cref="DockerFactAttribute"/>.</para>
/// </summary>
public class ContainerLifecycleTests : IClassFixture<ContainerLifecycleFixture>
{
    private readonly ContainerLifecycleFixture _fixture;

    public ContainerLifecycleTests(ContainerLifecycleFixture fixture) => _fixture = fixture;

    // ---- the run itself ----

    [DockerFact]
    public void Run_Succeeds_WhenTheContainerExitsZero()
    {
        Assert.True(_fixture.FinalRun.Status == RunStatus.Succeeded, _fixture.Diagnostics);
        Assert.Equal(0, _fixture.FinalRun.ExitCode);
        Assert.Null(_fixture.FinalRun.ErrorCode);
        Assert.NotNull(_fixture.FinalRun.StartedAt);
        Assert.NotNull(_fixture.FinalRun.FinishedAt);
    }

    /// <summary>
    /// The container read <c>/run/secrets/E2E_SECRET</c> and compared it byte-for-byte with the
    /// value that was stored through <c>POST /api/secrets</c>; a mismatch or a missing file exits
    /// non-zero with a distinct code (see <see cref="ContainerLifecycleFixture.ExitCodeMeanings"/>),
    /// which would land here as a failed run. This is the assertion the empty-secrets-dictionary
    /// encoding bug would have failed.
    /// </summary>
    [DockerFact]
    public void Secret_IsDeliveredToTheContainerAsAFile()
    {
        Assert.True(_fixture.FinalRun.Status == RunStatus.Succeeded, _fixture.Diagnostics);
        Assert.Contains("/run/secrets", string.Join(" ", _fixture.Container.HostConfig.Binds ?? new List<string>()));
        Assert.Contains(":ro", string.Join(" ", _fixture.Container.HostConfig.Binds ?? new List<string>()));
    }

    /// <summary>
    /// Secrets are file-only by design: container environment is readable by anyone who can call
    /// <c>docker inspect</c> and by every process in the container through <c>/proc/1/environ</c>.
    /// Asserted from both sides — the container itself refuses to continue if any <c>SECRET_*</c>
    /// variable exists, and the host re-checks the daemon's own view of the config.
    /// </summary>
    [DockerFact]
    public void Secrets_AreNeverExportedAsEnvironmentVariables()
    {
        var env = _fixture.Container.Config.Env ?? new List<string>();

        Assert.DoesNotContain(env, e => e.StartsWith("SECRET_", StringComparison.Ordinal));
        Assert.DoesNotContain(env, e => e.Contains(ContainerLifecycleFixture.SecretValue, StringComparison.Ordinal));
        Assert.Contains(env, e => e.StartsWith("AGENTHOST_RUN_TOKEN=", StringComparison.Ordinal));
        Assert.Contains(env, e => e == $"AGENTHOST_RUN_ID={_fixture.FinalRun.Id}");
    }

    /// <summary>The event the agent container POSTed with its run token, read back by a human.</summary>
    [DockerFact]
    public void AgentCallback_FromInsideTheContainer_IsReadableOnTheRunEventStream()
    {
        var evt = Assert.Single(_fixture.Events, e => e.EventType == ContainerLifecycleFixture.AgentEventType);

        Assert.Equal("info", evt.Level);
        Assert.True(evt.Seq > 0, "the event bus assigns a sequence number");

        // The container reported the number of bytes it read out of /run/secrets/<NAME>: proof the
        // mount carried the whole value, not just a non-empty file.
        var payload = Assert.IsType<JsonElement>(evt.Payload);
        Assert.Equal(ContainerLifecycleFixture.SecretValue.Length, payload.GetProperty("secretLength").GetInt32());
    }

    [DockerFact]
    public void Workspace_BindMount_IsWritableByTheAgent()
    {
        Assert.Equal("written-by-the-agent-container", _fixture.WorkspaceOutput?.Trim());
    }

    // ---- hardening, as the daemon recorded it ----

    [DockerFact]
    public void Container_IsCreatedWithTheHardeningFlags()
    {
        var hostConfig = _fixture.Container.HostConfig;

        Assert.True(hostConfig.ReadonlyRootfs, "read-only rootfs (spec 13.2)");
        Assert.Equal(new[] { "ALL" }, hostConfig.CapDrop);
        Assert.Contains("no-new-privileges=true", hostConfig.SecurityOpt);
        Assert.Contains("/tmp", hostConfig.Tmpfs.Keys);

        // network: full in the manifest, so bridge plus the one capability the orchestrator adds back.
        Assert.Equal("bridge", hostConfig.NetworkMode);
        Assert.Equal(new[] { "NET_BIND_SERVICE" }, hostConfig.CapAdd);
    }

    /// <summary>
    /// The manifest's <c>runtime.cpu</c>/<c>runtime.memory</c> reach the daemon as real limits.
    /// Compared against the run's own resolved profile rather than hardcoded numbers, so the
    /// assertion is about the plumbing, not about the fixture's manifest.
    /// </summary>
    [DockerFact]
    public void Container_IsCreatedWithTheManifestsResourceLimits()
    {
        var profile = _fixture.FinalRun.RuntimeProfile;
        var hostConfig = _fixture.Container.HostConfig;

        Assert.Equal(profile.Cpu * 1_000_000_000L, hostConfig.NanoCPUs);
        Assert.Equal(profile.MemoryBytes, hostConfig.Memory);
        Assert.Equal(profile.MemoryBytes, hostConfig.MemorySwap); // no swap
    }

    [DockerFact]
    public void Container_CarriesTheRunLabels()
    {
        var labels = _fixture.Container.Config.Labels;

        Assert.Equal(_fixture.FinalRun.Id, labels["agenthost.run_id"]);
        Assert.Equal(_fixture.FinalRun.ProjectId, labels["agenthost.project_id"]);
        Assert.Equal(_fixture.FinalRun.AgentId, labels["agenthost.agent_id"]);
        Assert.Equal("bridge", labels["agenthost.network"]);
    }

    // ---- cleanup, which is a security guarantee ----

    [DockerFact]
    public void PlaintextSecrets_AreDeletedFromDiskWhenTheContainerExits()
    {
        Assert.True(_fixture.SecretsDirectoryExistedDuringTheRun,
            "the secrets directory must have existed while the container was running");
        Assert.False(Directory.Exists(_fixture.SecretsDirectory),
            $"plaintext secrets are still on disk at {_fixture.SecretsDirectory}");
    }

    [DockerFact]
    public void Container_IsRemovedAfterTheRunFinishes()
    {
        Assert.True(_fixture.ContainerRemoved,
            $"container {_fixture.Container.ID} was still present after the run reached a terminal state");
    }
}

/// <summary>
/// The parts of the end-to-end fixture that can be checked without a daemon: the build context it
/// would send, and the agent script inside it. These run everywhere — a broken tar or a script that
/// lost its API address would otherwise only be discovered on a machine that has Docker.
/// </summary>
public class ContainerBuildContextTests
{
    [Fact]
    public void BuildContext_ContainsADockerfileAndAnExecutableAgentScript()
    {
        using var context = ContainerLifecycleFixture.BuildContextTar("http://172.17.0.1:41234");
        using var reader = new TarReader(context);

        var entries = new Dictionary<string, (UnixFileMode Mode, string Content)>();
        while (reader.GetNextEntry() is { } entry)
        {
            using var data = new StreamReader(entry.DataStream!);
            entries[entry.Name] = (entry.Mode, data.ReadToEnd());
        }

        Assert.Equal(new[] { "Dockerfile", "agent.sh" }, entries.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Contains("FROM alpine:3.20", entries["Dockerfile"].Content);
        Assert.Contains("""CMD ["/bin/sh", "/agent.sh"]""", entries["Dockerfile"].Content);
        Assert.True(entries["agent.sh"].Mode.HasFlag(UnixFileMode.UserExecute));
    }

    /// <summary>
    /// The script is generated per test run because it carries the address of that run's listener;
    /// a placeholder surviving into the image would produce a container that dials nowhere.
    /// </summary>
    [Fact]
    public void AgentScript_IsFullyResolved()
    {
        var script = ContainerLifecycleFixture.AgentScript("http://172.17.0.1:41234");

        Assert.Contains("API_BASE='http://172.17.0.1:41234'", script);
        Assert.Contains($"/run/secrets/{ContainerLifecycleFixture.SecretName}", script);
        Assert.Contains(ContainerLifecycleFixture.SecretValue, script);
        Assert.Contains(ContainerLifecycleFixture.AgentEventType, script);
        Assert.DoesNotContain("__", script);
        Assert.DoesNotContain("\r", script);
    }
}

/// <summary>
/// Runs the end-to-end scenario exactly once for the whole class: builds the agent image, creates
/// the org/project/secret/agent fixture, starts the run, snapshots the live container, releases it,
/// and collects everything the tests assert on. A missing container runtime makes this a no-op —
/// xUnit constructs class fixtures even when every test in the class is skipped.
/// </summary>
public sealed class ContainerLifecycleFixture : IAsyncLifetime
{
    public const string SecretName = "E2E_SECRET";
    public const string SecretValue = "s3cr3t-value-only-the-container-can-confirm";
    public const string AgentEventType = "agent.e2e.contract";
    public const string OutputFileName = "agent-output.txt";
    public const string ReleaseFileName = "host-says-you-may-exit";

    /// <summary>
    /// What each non-zero exit from the agent script means. Surfaced in the failure message because
    /// the container is gone (and its logs with it) by the time an assertion runs.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> ExitCodeMeanings = new Dictionary<int, string>
    {
        [20] = "AGENTHOST_RUN_ID was not set in the container",
        [21] = "AGENTHOST_RUN_TOKEN was not set in the container",
        [22] = $"/run/secrets/{SecretName} did not exist (secret delivery failed)",
        [23] = "the secret file's contents did not match the stored secret",
        [24] = "a SECRET_* environment variable was present (secrets must be file-only)",
        [25] = "/workspace was not writable",
        [26] = "the container's root filesystem was writable",
        [28] = "the container could not reach POST /api/agent/runs/{id}/events, or was refused by it — " +
               "tried BusyBox wget and a raw socket, five and three times",
    };

    private static readonly TimeSpan LaunchTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(60);

    private KestrelAgentHostApiFactory? _factory;
    private DockerClient? _docker;
    private string? _imageTag;
    private string _workspaceRoot = string.Empty;

    // ---- what the tests read ----

    public Run FinalRun { get; private set; } = default!;
    public ContainerInspectResponse Container { get; private set; } = default!;
    public List<RunEvent> Events { get; private set; } = new();
    public string SecretsDirectory { get; private set; } = string.Empty;
    public bool SecretsDirectoryExistedDuringTheRun { get; private set; }
    public bool ContainerRemoved { get; private set; }
    public string? WorkspaceOutput { get; private set; }

    /// <summary>
    /// One line explaining how the run actually ended, for assertions that expect it to have
    /// succeeded. The container (and therefore its logs) is gone by the time a test runs, so the
    /// agent's exit code — each value meaning one broken clause of the contract — is the evidence.
    /// </summary>
    public string Diagnostics => Describe(FinalRun, "the agent container did not complete the contract");

    public async Task InitializeAsync()
    {
        // No daemon: leave every field untouched. Every test in the class is skipped by
        // DockerFactAttribute, so nothing reads them.
        if (!ContainerRuntime.IsAvailable)
            return;

        _workspaceRoot = Path.Combine(Path.GetTempPath(), "agenthost-e2e-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_workspaceRoot);

        _factory = new KestrelAgentHostApiFactory(_workspaceRoot);
        // Use the application's own Docker client, resolved through the application's own
        // configuration: the test and the code under test must be talking to the same daemon.
        _docker = _factory.Services.GetRequiredService<DockerClient>();

        var apiBaseUrl = await ResolveContainerFacingApiUrlAsync(_docker, _factory.ListeningPort);
        _imageTag = $"localhost:5000/agenthost-e2e-agent:{Guid.NewGuid().ToString("N")[..12]}";
        await BuildAgentImageAsync(_docker, _imageTag, apiBaseUrl);

        // ---- org / project / secret / agent, all through the real API ----
        var suffix = TestData.Suffix();
        var auth = await TestData.RegisterAsync(_factory.CreateClient(), suffix);
        var client = TestData.AuthedClient(_factory, auth.Token);
        var project = await TestData.CreateProjectAsync(client, suffix);

        var secretResponse = await client.PostJsonAsync("/api/secrets", new CreateSecretRequest
        {
            Name = SecretName,
            Value = SecretValue,
            Scope = SecretScope.Org,
        });
        Assert.Equal(HttpStatusCode.Created, secretResponse.StatusCode);

        var agentResponse = await client.PostJsonAsync("/api/agents", new CreateAgentRequest
        {
            ProjectId = project.Id,
            Name = $"E2E Container Agent {suffix}",
            Slug = $"e2e-container-agent-{suffix}",
            ManifestYaml = ManifestYaml($"e2e-container-agent-{suffix}", _imageTag),
            Publish = true,
        });
        agentResponse.EnsureSuccessStatusCode();
        var agent = (await agentResponse.Content.ReadFromJsonAsync<Agent>(TestJson.Options))!;

        // ---- the run: from here on nothing is simulated ----
        var runResponse = await client.PostJsonAsync("/api/runs", new CreateRunRequest
        {
            AgentId = agent.Id,
            Inputs = new JsonObject { ["scenario"] = "container-lifecycle" },
        });
        Assert.Equal(HttpStatusCode.Created, runResponse.StatusCode);
        var run = (await runResponse.Content.ReadFromJsonAsync<Run>(TestJson.Options))!;

        var runDirectory = Path.Combine(_workspaceRoot, run.Id);
        SecretsDirectory = Path.Combine(runDirectory, "secrets");
        var workspaceDirectory = Path.Combine(runDirectory, "workspace");

        // Snapshot the container while it is alive — the orchestrator removes it seconds later.
        Container = await InspectLaunchedContainerAsync(_docker, run.Id, client);
        SecretsDirectoryExistedDuringTheRun = Directory.Exists(SecretsDirectory);

        // Release the agent: it is blocked waiting for this file inside its /workspace bind mount.
        await File.WriteAllTextAsync(Path.Combine(workspaceDirectory, ReleaseFileName), "go");

        FinalRun = await WaitForTerminalRunAsync(client, run.Id);
        ContainerRemoved = await WaitForContainerRemovalAsync(_docker, run.Id);

        Events = (await client.GetFromJsonAsync<List<RunEvent>>($"/api/runs/{run.Id}/events", TestJson.Options))!;

        var outputPath = Path.Combine(workspaceDirectory, OutputFileName);
        WorkspaceOutput = File.Exists(outputPath) ? await File.ReadAllTextAsync(outputPath) : null;
    }

    public async Task DisposeAsync()
    {
        if (_docker is not null && _imageTag is not null)
        {
            try
            {
                await _docker.Images.DeleteImageAsync(_imageTag, new ImageDeleteParameters { Force = true }, CancellationToken.None);
            }
            catch (Exception)
            {
                // Best effort: a leftover 8 MB image tag on a CI runner is not worth failing a test over.
            }
        }

        if (_factory is not null)
            await _factory.DisposeAsync();

        try
        {
            if (_workspaceRoot.Length > 0 && Directory.Exists(_workspaceRoot))
                Directory.Delete(_workspaceRoot, recursive: true);
        }
        catch (Exception)
        {
            // ditto.
        }
    }

    // ---- the agent image ----

    /// <summary>
    /// The address the container dials. On a Linux daemon <c>host.docker.internal</c> does not
    /// exist; the host is reachable from a bridge-networked container at the bridge's gateway
    /// address, which is read from the daemon rather than assumed to be 172.17.0.1.
    /// </summary>
    private static async Task<string> ResolveContainerFacingApiUrlAsync(DockerClient docker, int port)
    {
        var bridge = await docker.Networks.InspectNetworkAsync("bridge", CancellationToken.None);
        var gateway = bridge.IPAM?.Config?
            .Select(c => c.Gateway)
            .FirstOrDefault(g => !string.IsNullOrWhiteSpace(g));

        if (string.IsNullOrWhiteSpace(gateway))
        {
            throw new InvalidOperationException(
                "The daemon's bridge network reports no gateway address, so the agent container has no " +
                "route back to the API. Inspect `docker network inspect bridge`.");
        }

        return $"http://{gateway}:{port}";
    }

    /// <summary>
    /// Builds the agent image through the daemon's build endpoint from an in-memory tar context
    /// (a Dockerfile plus the agent script). <c>alpine</c> is the base because its BusyBox
    /// <c>wget</c> supports <c>--post-data</c>, which is all the agent needs to speak the callback
    /// protocol — no package installation, so the build needs no network beyond the base image.
    /// </summary>
    private static async Task BuildAgentImageAsync(DockerClient docker, string tag, string apiBaseUrl)
    {
        using var context = BuildContextTar(apiBaseUrl);

        var output = new List<string>();
        await docker.Images.BuildImageFromDockerfileAsync(
            new ImageBuildParameters { Dockerfile = "Dockerfile", Tags = new List<string> { tag }, Remove = true, ForceRemove = true },
            context,
            Array.Empty<AuthConfig>(),
            new Dictionary<string, string>(),
            new Progress<JSONMessage>(m =>
            {
                if (!string.IsNullOrWhiteSpace(m.Stream)) output.Add(m.Stream.Trim());
                if (!string.IsNullOrWhiteSpace(m.ErrorMessage)) output.Add("ERROR: " + m.ErrorMessage.Trim());
            }),
            CancellationToken.None);

        try
        {
            // The build endpoint reports failures in its output stream, not as an exception.
            await docker.Images.InspectImageAsync(tag, CancellationToken.None);
        }
        catch (DockerImageNotFoundException ex)
        {
            throw new InvalidOperationException(
                $"Building the agent image {tag} failed. Build output:{Environment.NewLine}" +
                string.Join(Environment.NewLine, output), ex);
        }
    }

    /// <summary>
    /// The build context the daemon receives: a tar of a two-line Dockerfile and the agent script.
    /// Built in memory — there is no fixture image checked into the repository and nothing is
    /// pushed anywhere. Exposed to <see cref="ContainerBuildContextTests"/>, which checks it
    /// without needing a daemon.
    /// </summary>
    internal static MemoryStream BuildContextTar(string apiBaseUrl)
    {
        const string dockerfile = """
            FROM alpine:3.20
            COPY agent.sh /agent.sh
            CMD ["/bin/sh", "/agent.sh"]
            """;

        var context = new MemoryStream();
        using (var tar = new TarWriter(context, TarEntryFormat.Ustar, leaveOpen: true))
        {
            AddFile(tar, "Dockerfile", dockerfile,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            AddFile(tar, "agent.sh", AgentScript(apiBaseUrl),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        context.Position = 0;
        return context;
    }

    private static void AddFile(TarWriter tar, string name, string content, UnixFileMode mode)
    {
        // The daemon's builder and /bin/sh both want LF, whatever the host wrote the literal with.
        var bytes = Encoding.UTF8.GetBytes(content.ReplaceLineEndings("\n"));
        tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = new MemoryStream(bytes),
            Mode = mode,
        });
    }

    /// <summary>
    /// The agent. It proves the four halves of the contract the host cannot prove for it — the
    /// secret arrives as a file with the right bytes and *only* as a file, /workspace is writable,
    /// the rootfs is not, and the run token authenticates a callback made from inside the
    /// container's own network namespace — then blocks until the host releases it and exits 0.
    /// Each failure gets its own exit code (see <see cref="ExitCodeMeanings"/>) so a broken
    /// contract is diagnosable from the run row alone, after the container and its logs are gone.
    /// </summary>
    internal static string AgentScript(string apiBaseUrl) => """
        #!/bin/sh
        API_BASE='__API_BASE__'
        EXPECTED_SECRET='__SECRET_VALUE__'
        SECRET_FILE='/run/secrets/__SECRET_NAME__'
        RELEASE_FILE='/workspace/__RELEASE_FILE__'

        echo "agent: run=$AGENTHOST_RUN_ID protocol=$AGENTHOST_PROTOCOL_VERSION api=$API_BASE"

        [ -n "$AGENTHOST_RUN_ID" ] || { echo 'agent: AGENTHOST_RUN_ID missing'; exit 20; }
        [ -n "$AGENTHOST_RUN_TOKEN" ] || { echo 'agent: AGENTHOST_RUN_TOKEN missing'; exit 21; }
        [ -f "$SECRET_FILE" ] || { echo "agent: $SECRET_FILE missing"; exit 22; }

        ACTUAL_SECRET=$(cat "$SECRET_FILE")
        [ "$ACTUAL_SECRET" = "$EXPECTED_SECRET" ] || { echo 'agent: secret value mismatch'; exit 23; }

        if env | grep -q '^SECRET_'; then echo 'agent: SECRET_* environment variable present'; exit 24; fi

        echo 'written-by-the-agent-container' > /workspace/__OUTPUT_FILE__ || { echo 'agent: /workspace not writable'; exit 25; }
        if echo probe 2>/dev/null > /rootfs-write-probe; then echo 'agent: rootfs is writable'; exit 26; fi

        SECRET_LENGTH=$(printf %s "$ACTUAL_SECRET" | wc -c | tr -d ' ')
        BODY='{"eventType":"__EVENT_TYPE__","level":"info","message":"secret read, workspace written","payload":{"secretLength":'"$SECRET_LENGTH"'}}'
        EVENTS_URL="$API_BASE/api/agent/runs/$AGENTHOST_RUN_ID/events"

        posted=0
        n=0
        while [ $n -lt 5 ]; do
            if wget -q -O - \
                --header="Authorization: Bearer $AGENTHOST_RUN_TOKEN" \
                --header="Content-Type: application/json" \
                --post-data="$BODY" \
                "$EVENTS_URL"; then
                posted=1
                echo ''
                echo 'agent: callback accepted (wget)'
                break
            fi
            n=$((n + 1))
            sleep 2
        done

        # Fallback for a BusyBox built without wget's long options: the same request, spoken
        # directly over TCP. Either path proves the same thing — a run-token-authenticated call
        # from inside the container.
        if [ $posted -eq 0 ]; then
            echo 'agent: wget callback failed, retrying over a raw socket'
            HOST_PORT=${API_BASE#http://}
            HOST=${HOST_PORT%%:*}
            PORT=${HOST_PORT##*:}
            LENGTH=$(printf %s "$BODY" | wc -c | tr -d ' ')
            n=0
            while [ $n -lt 3 ]; do
                if printf 'POST /api/agent/runs/%s/events HTTP/1.0\r\nHost: %s\r\nAuthorization: Bearer %s\r\nContent-Type: application/json\r\nContent-Length: %s\r\n\r\n%s' \
                    "$AGENTHOST_RUN_ID" "$HOST_PORT" "$AGENTHOST_RUN_TOKEN" "$LENGTH" "$BODY" \
                    | nc "$HOST" "$PORT" | head -n 1 | grep -q ' 200'; then
                    posted=1
                    echo 'agent: callback accepted (raw socket)'
                    break
                fi
                n=$((n + 1))
                sleep 2
            done
        fi

        [ $posted -eq 1 ] || { echo "agent: callback to $EVENTS_URL failed"; exit 28; }

        # Wait for the host to finish inspecting this container. Bounded: a host that never
        # releases us must not hang the run past its maxDuration.
        i=0
        while [ ! -f "$RELEASE_FILE" ] && [ $i -lt 120 ]; do
            sleep 1
            i=$((i + 1))
        done
        echo "agent: released after ${i}s"
        exit 0
        """
        .Replace("__API_BASE__", apiBaseUrl)
        .Replace("__SECRET_VALUE__", SecretValue)
        .Replace("__SECRET_NAME__", SecretName)
        .Replace("__RELEASE_FILE__", ReleaseFileName)
        .Replace("__OUTPUT_FILE__", OutputFileName)
        .Replace("__EVENT_TYPE__", AgentEventType)
        // /bin/sh will not run a script with CRLF line endings, and this literal's endings are
        // whatever the checkout produced.
        .ReplaceLineEndings("\n");

    private static string ManifestYaml(string name, string imageTag) => $"""
        apiVersion: agenthost.dev/v1
        kind: Agent
        metadata:
          name: {name}
          displayName: {name}
          description: End-to-end container lifecycle agent
        spec:
          type: oci
          image: {imageTag}
          permissions:
            network: full
            secrets:
              - {SecretName}
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

    // ---- polling ----

    /// <summary>
    /// Waits for the orchestrator's container to exist and inspects it. The launch is asynchronous
    /// (RunService starts it on a background scope) and includes a doomed image pull, so this can
    /// take a few seconds. A run that reaches a terminal state without a container to inspect means
    /// the launch itself failed — reported with the run's own error fields, since there is no
    /// container left to read logs from.
    /// </summary>
    private static async Task<ContainerInspectResponse> InspectLaunchedContainerAsync(
        DockerClient docker, string runId, HttpClient client)
    {
        var deadline = DateTime.UtcNow + LaunchTimeout;
        while (DateTime.UtcNow < deadline)
        {
            var containers = await ListRunContainersAsync(docker, runId);
            if (containers.Count > 0)
                return await docker.Containers.InspectContainerAsync(containers[0].ID, CancellationToken.None);

            var run = await GetRunAsync(client, runId);
            if (run.Status.IsTerminal())
                throw new InvalidOperationException(Describe(run, "no container was ever created for the run"));

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"No container carrying label agenthost.run_id={runId} appeared within {LaunchTimeout.TotalSeconds:0}s.");
    }

    private static async Task<Run> WaitForTerminalRunAsync(HttpClient client, string runId)
    {
        var deadline = DateTime.UtcNow + FinishTimeout;
        Run? run = null;
        while (DateTime.UtcNow < deadline)
        {
            run = await GetRunAsync(client, runId);
            if (run.Status.IsTerminal())
                return run;

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"Run {runId} did not reach a terminal state within {FinishTimeout.TotalSeconds:0}s " +
            $"(last status: {run?.Status.ToDbString()}).");
    }

    private static async Task<bool> WaitForContainerRemovalAsync(DockerClient docker, string runId)
    {
        var deadline = DateTime.UtcNow + CleanupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await ListRunContainersAsync(docker, runId)).Count == 0)
                return true;

            await Task.Delay(250);
        }

        return false;
    }

    private static Task<IList<ContainerListResponse>> ListRunContainersAsync(DockerClient docker, string runId) =>
        docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                All = true,
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    ["label"] = new Dictionary<string, bool> { [$"agenthost.run_id={runId}"] = true },
                },
            },
            CancellationToken.None);

    private static async Task<Run> GetRunAsync(HttpClient client, string runId) =>
        (await client.GetFromJsonAsync<Run>($"/api/runs/{runId}", TestJson.Options))!;

    /// <summary>
    /// Renders a failed run for a human, translating the agent's exit code into the clause of the
    /// contract that broke.
    /// </summary>
    private static string Describe(Run run, string what)
    {
        var meaning = run.ExitCode is { } code && ExitCodeMeanings.TryGetValue(code, out var m)
            ? $" — exit {code}: {m}"
            : run.ExitCode is { } other ? $" — exit {other}" : string.Empty;

        return $"{what}: status={run.Status.ToDbString()}{meaning}, " +
               $"errorCode={run.ErrorCode ?? "none"}, errorMessage={run.ErrorMessage ?? "none"}";
    }
}
