using System.Net;
using System.Net.Http.Json;
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
/// an image: <c>alpine</c> plus a shell script, committed at test time into a throwaway local tag.
/// No Dockerfile, no builder, no registry, no push, no fixture image in the repository.</para>
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
        // The container's own comparison passed, which is what makes the run succeed at all.
        Assert.True(_fixture.FinalRun.Status == RunStatus.Succeeded, _fixture.Diagnostics);

        // ...and the mount it read through is the read-only bind of the directory whose deletion
        // PlaintextSecrets_AreDeletedFromDiskWhenTheContainerExits then requires.
        var binds = _fixture.Container.HostConfig.Binds ?? new List<string>();
        var secretsBind = Assert.Single(binds, b => b.Contains(":/run/secrets", StringComparison.Ordinal));
        Assert.StartsWith(_fixture.SecretsDirectory + ":", secretsBind);
        Assert.EndsWith(":ro", secretsBind);
        Assert.Contains(binds, b => b.Contains(":/workspace", StringComparison.Ordinal));
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
        Assert.True(
            _fixture.Events.Any(e => e.EventType == ContainerLifecycleFixture.AgentEventType),
            $"the container's event never reached the run event stream. {_fixture.Diagnostics}. " +
            $"Events seen: {string.Join(", ", _fixture.Events.Select(e => e.EventType))}");

        var evt = _fixture.Events.First(e => e.EventType == ContainerLifecycleFixture.AgentEventType);

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
        Assert.Contains("/tmp", hostConfig.Tmpfs.Keys);

        // Capability and security-option spellings are compared loosely on purpose: what matters is
        // that the daemon recorded "drop everything" and "no new privileges", and engines differ on
        // whether they echo CAP_ prefixes or normalize the option's `=true` form.
        Assert.Contains(hostConfig.CapDrop, c => Capability(c) == "ALL");
        Assert.Contains(hostConfig.SecurityOpt, o => o.Contains("no-new-privileges", StringComparison.OrdinalIgnoreCase));

        // network: full in the manifest, so bridge plus the one capability the orchestrator adds back.
        Assert.Equal("bridge", hostConfig.NetworkMode);
        Assert.Contains(hostConfig.CapAdd, c => Capability(c) == "NET_BIND_SERVICE");
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

        // MemorySwap == Memory is how "no swap" is expressed. A daemon whose kernel has no swap
        // accounting (no swapaccount=1 — the default on many hosts, including GitHub's runners)
        // rejects the pairing and rewrites it to -1, logging "No swap limit support" at startup.
        // That is the platform's answer, not the orchestrator's, so both are accepted here — and
        // the second one is worth knowing about: on such a host the memory cap does not cover swap.
        Assert.True(
            hostConfig.MemorySwap == profile.MemoryBytes || hostConfig.MemorySwap == -1,
            $"expected MemorySwap == Memory ({profile.MemoryBytes}) or -1 (daemon without swap " +
            $"accounting), got {hostConfig.MemorySwap}");
    }

    /// <summary>Capability name without the engine-dependent <c>CAP_</c> prefix, upper-cased.</summary>
    private static string Capability(string name)
    {
        var trimmed = name.Trim().ToUpperInvariant();
        return trimmed.StartsWith("CAP_", StringComparison.Ordinal) ? trimmed[4..] : trimmed;
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
/// The part of the end-to-end fixture that can be checked without a daemon: the agent script that
/// gets baked into the image. It runs everywhere — a script that lost its API address, its secret
/// name or its line endings would otherwise only be discovered on a machine that has Docker.
/// </summary>
public class AgentScriptTests
{
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

    public const string BaseImageRepository = "alpine";
    public const string BaseImageTag = "3.20";

    /// <summary>
    /// Repository for the image built per test run. The <c>localhost:5000/</c> prefix is load
    /// bearing: the orchestrator pulls every image it launches, that pull cannot succeed for a
    /// local-only tag, and this way it fails against a refused connection in milliseconds instead
    /// of doing a round trip to Docker Hub (and counting against its anonymous rate limit).
    /// </summary>
    public const string ImageRepository = "localhost:5000/agenthost-e2e-agent";

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
        _imageTag = $"{ImageRepository}:{Guid.NewGuid().ToString("N")[..12]}";
        await CreateAgentImageAsync(_docker, _imageTag, apiBaseUrl);

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

        // The run's two directories as the *backend* computes them, not as the test guesses them:
        // this is the same ContainerPathMapper instance the orchestrator wrote through.
        var paths = _factory.Services.GetRequiredService<ContainerPathMapper>();
        SecretsDirectory = paths.LocalSecretsDirectory(run.Id);
        var workspaceDirectory = paths.LocalWorkspaceDirectory(run.Id);

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
    /// Produces the agent image: pull <c>alpine</c>, create a container whose command is the agent
    /// script, commit it, throw the container away. The script has to live in the *image* rather
    /// than be passed at create time because <see cref="Services.ContainerOrchestrator"/> sets no
    /// <c>Cmd</c> — a real agent image carries its own entrypoint — and this test must not bend the
    /// product to make itself possible.
    ///
    /// <para>Commit rather than <c>/build</c>: it is one stable API call against an image that is
    /// already local, needs no build context and no builder (the classic builder behind the build
    /// endpoint is deprecated), and produces a layerless image in milliseconds.</para>
    ///
    /// <para><c>alpine</c> is the base because its BusyBox <c>wget</c> supports
    /// <c>--post-data</c> — everything the agent needs to speak the callback protocol, with nothing
    /// to install.</para>
    /// </summary>
    private static async Task CreateAgentImageAsync(DockerClient docker, string tag, string apiBaseUrl)
    {
        await docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = BaseImageRepository, Tag = BaseImageTag },
            null,
            new Progress<JSONMessage>(_ => { }),
            CancellationToken.None);

        var command = new[] { "/bin/sh", "-c", AgentScript(apiBaseUrl) };

        var scaffold = await docker.Containers.CreateContainerAsync(
            new CreateContainerParameters { Image = $"{BaseImageRepository}:{BaseImageTag}", Cmd = command },
            CancellationToken.None);

        try
        {
            await docker.Images.CommitContainerChangesAsync(
                new CommitContainerChangesParameters
                {
                    ContainerID = scaffold.ID,
                    RepositoryName = ImageRepository,
                    Tag = tag.Split(':').Last(),
                    Comment = "AgentHost end-to-end contract agent",
                    Config = new Config { Cmd = command },
                },
                CancellationToken.None);
        }
        finally
        {
            await docker.Containers.RemoveContainerAsync(
                scaffold.ID, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
        }

        // Fail here, with the tag in hand, rather than inside the orchestrator's pull-and-shrug path.
        await docker.Images.InspectImageAsync(tag, CancellationToken.None);
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
