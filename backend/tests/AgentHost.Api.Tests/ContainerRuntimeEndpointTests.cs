using AgentHost.Api.Infrastructure;
using AgentHost.Api.Services;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// Socket discovery and the runtime-neutral pieces of the orchestrator's HostConfig. Docker and
/// Podman differ in where the socket lives, not in the API spoken over it, so this is the whole of
/// the "which runtime" question that code actually has to answer.
/// </summary>
public class ContainerRuntimeEndpointTests
{
    private static Func<string, bool> Existing(params string[] paths) =>
        path => paths.Contains(path);

    [Fact]
    public void ExplicitConfiguration_Wins_OverEverythingElse()
    {
        var endpoint = ContainerRuntimeEndpoint.Resolve(
            "tcp://docker-socket-proxy:2375", "unix:///run/podman/podman.sock", null, 0,
            Existing(ContainerRuntimeEndpoint.RootfulPodmanSocket));

        Assert.Equal("tcp://docker-socket-proxy:2375", endpoint.Uri);
        Assert.Equal("Docker:Host", endpoint.Source);
    }

    [Fact]
    public void DockerHostEnvironmentVariable_IsUsedWhenNothingIsConfigured()
    {
        var endpoint = ContainerRuntimeEndpoint.Resolve(
            null, "unix:///run/user/1000/podman/podman.sock", null, 1000, Existing());

        Assert.Equal("unix:///run/user/1000/podman/podman.sock", endpoint.Uri);
        Assert.Equal("DOCKER_HOST", endpoint.Source);
        Assert.Equal(ContainerRuntimeKind.Podman, endpoint.Runtime);
    }

    [Fact]
    public void AsRoot_TheRootfulPodmanSocketIsFound_WhenThereIsNoDockerSocket()
    {
        var endpoint = ContainerRuntimeEndpoint.Resolve(
            null, null, null, 0, Existing(ContainerRuntimeEndpoint.RootfulPodmanSocket));

        Assert.Equal("unix:///run/podman/podman.sock", endpoint.Uri);
        Assert.Equal("autodetected", endpoint.Source);
        Assert.Equal(ContainerRuntimeKind.Podman, endpoint.Runtime);
    }

    [Fact]
    public void AsNonRoot_TheRootlessSocketWins_EvenWhenARootfulOneExists()
    {
        // /var/run/docker.sock is typically 0660 root:docker: as uid 1000 the rootless Podman
        // socket is the one that can actually be opened, so it must be probed first.
        var endpoint = ContainerRuntimeEndpoint.Resolve(
            null, null, null, 1000,
            Existing(ContainerRuntimeEndpoint.DockerSocket, "/run/user/1000/podman/podman.sock"));

        Assert.Equal("unix:///run/user/1000/podman/podman.sock", endpoint.Uri);
    }

    [Fact]
    public void XdgRuntimeDir_OverridesTheDerivedRootlessLocation()
    {
        var endpoint = ContainerRuntimeEndpoint.Resolve(
            null, null, "/tmp/run-1000/", 1000, Existing("/tmp/run-1000/podman/podman.sock"));

        Assert.Equal("unix:///tmp/run-1000/podman/podman.sock", endpoint.Uri);
    }

    [Fact]
    public void WithNoSocketAnywhere_TheHistoricalDefaultIsKept()
    {
        var endpoint = ContainerRuntimeEndpoint.Resolve(null, null, null, 0, Existing());

        Assert.Equal(ContainerRuntimeEndpoint.FallbackUri, endpoint.Uri);
        Assert.Equal("default", endpoint.Source);
    }

    [Theory]
    [InlineData("unix:///run/podman/podman.sock", ContainerRuntimeKind.Podman)]
    [InlineData("unix:///var/run/docker.sock", ContainerRuntimeKind.Docker)]
    [InlineData("tcp://socket-proxy:2375", ContainerRuntimeKind.Unknown)]
    public void RuntimeIsInferredFromTheEndpoint_ForLoggingOnly(string uri, ContainerRuntimeKind expected)
    {
        Assert.Equal(expected, ContainerRuntimeEndpoint.Classify(uri));
    }

    /// <summary>
    /// HostConfig.CPUCount is a Windows-container field: both the Linux Docker daemon and Podman
    /// ignore it, so the manifest's runtime.cpu used to constrain nothing at all. NanoCPUs is the
    /// portable Linux mechanism.
    /// </summary>
    [Theory]
    [InlineData(2, 2_000_000_000L)]
    [InlineData(1, 1_000_000_000L)]
    [InlineData(16, 16_000_000_000L)]
    [InlineData(0, 0L)]
    [InlineData(-1, 0L)]
    public void CpuCoresBecomeNanoCpus(long cores, long expected)
    {
        Assert.Equal(expected, ContainerOrchestrator.NanoCpus(cores));
    }
}
