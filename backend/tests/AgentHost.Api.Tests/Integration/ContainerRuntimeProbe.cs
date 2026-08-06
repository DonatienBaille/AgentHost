using AgentHost.Shared.Containers;
using AgentHost.Api.Infrastructure;
using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// One-shot probe for a reachable container daemon, resolved exactly the way the product resolves
/// it (<see cref="ContainerRuntimeEndpoint"/>: <c>Docker:Host</c> → <c>DOCKER_HOST</c> → the
/// well-known sockets), so a test that says "no runtime" is saying the same thing the backend
/// would say at launch time.
///
/// The result is cached for the lifetime of the test process: the probe runs once, during test
/// discovery/first use, and costs one <c>/_ping</c> round trip (or one failed connect).
/// </summary>
public static class ContainerRuntime
{
    private static readonly Lazy<string?> Probe = new(ProbeForRuntime, isThreadSafe: true);

    /// <summary>Null when a daemon answered; otherwise a human-readable reason to skip.</summary>
    public static string? SkipReason => Probe.Value;

    public static bool IsAvailable => Probe.Value is null;

    /// <summary>
    /// The endpoint the probe uses — resolved from the same settings, in the same precedence, that
    /// the host under test resolves it from (the API project's appsettings are copied next to the
    /// test assembly, and environment variables win over them exactly as in Program.cs). Probing a
    /// different socket than the backend will use would turn "no runtime" into a false skip or, far
    /// worse, a false start.
    /// </summary>
    public static ContainerRuntimeEndpoint Endpoint { get; } = ContainerRuntimeEndpoint.Resolve(
        new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build());

    public static DockerClient CreateClient() =>
        new DockerClientConfiguration(new Uri(Endpoint.Uri)).CreateClient();

    private static string? ProbeForRuntime()
    {
        try
        {
            using var client = CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            client.System.PingAsync(timeout.Token).GetAwaiter().GetResult();
            return null;
        }
        catch (Exception ex)
        {
            return $"No container runtime is reachable at {Endpoint.Uri} " +
                   $"(resolved from: {Endpoint.Source}). This test needs a real Docker/Podman daemon — " +
                   $"it runs in CI on ubuntu-latest. {ex.GetType().Name}: {ex.Message}";
        }
    }
}

/// <summary>
/// <see cref="FactAttribute"/> that reports the test as SKIPPED — never as passed and never as
/// failed — when no container daemon answers. Skipping is deliberate: a container test that
/// silently "passes" without a runtime is exactly the illusion this suite exists to remove, and a
/// hard failure would make the repository untestable on any machine without Docker.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DockerFactAttribute : FactAttribute
{
    public override string? Skip
    {
        get => ContainerRuntime.SkipReason ?? base.Skip;
        set => base.Skip = value;
    }
}
