using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;

namespace AgentHost.Shared.Containers;

/// <summary>Which engine is answering on the resolved socket, as far as its path lets us tell.</summary>
public enum ContainerRuntimeKind
{
    /// <summary>A socket whose path says nothing (a TCP endpoint, a socket proxy, a custom path).</summary>
    Unknown,
    Docker,
    Podman,
}

/// <summary>
/// The container API endpoint the orchestrator drives, and where the value came from.
///
/// <para><b>Why there is no <c>Docker:Runtime</c> setting.</b> Podman ships a Docker-compatible REST
/// API, and every call the orchestrator makes (images/create, containers/create, start, wait, logs,
/// remove, list-by-label) is served by that compat endpoint with the same request bodies. One code
/// path therefore drives both engines, so an engine-declaring knob would only be a way for an
/// operator to get it wrong. What genuinely differs is (a) the socket path, resolved here, and
/// (b) mount ownership/labelling on rootless setups, exposed as <c>Docker:BindMountOptions</c>
/// (see <see cref="ContainerPathMapper"/>). <see cref="Runtime"/> is informational: it is logged so
/// an operator can confirm what was picked up, and it is never branched on.</para>
/// </summary>
/// <param name="Uri">Endpoint URI for Docker.DotNet, e.g. <c>unix:///run/podman/podman.sock</c>.</param>
/// <param name="Runtime">Engine inferred from the endpoint path. Informational only.</param>
/// <param name="Source">Human-readable origin of the value, for the startup log.</param>
public sealed record ContainerRuntimeEndpoint(string Uri, ContainerRuntimeKind Runtime, string Source)
{
    public const string DockerSocket = "/var/run/docker.sock";
    public const string RootfulPodmanSocket = "/run/podman/podman.sock";
    public const string FallbackUri = "unix://" + DockerSocket;

    /// <summary>
    /// Resolution order: explicit <c>Docker:Host</c>, then <c>DOCKER_HOST</c> (which
    /// <c>podman system service</c> users and rootless Docker both set), then the well-known sockets
    /// in the order that suits the current uid. When nothing is found the historical default
    /// (<c>unix:///var/run/docker.sock</c>) is kept, so an unconfigured install behaves as before and
    /// fails at the first API call rather than at startup.
    /// </summary>
    public static ContainerRuntimeEndpoint Resolve(IConfiguration config) => Resolve(
        config["Docker:Host"],
        Environment.GetEnvironmentVariable("DOCKER_HOST"),
        Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
        CurrentUid(),
        File.Exists);

    internal static ContainerRuntimeEndpoint Resolve(
        string? configuredHost,
        string? dockerHostEnv,
        string? xdgRuntimeDir,
        uint uid,
        Func<string, bool> socketExists)
    {
        if (!string.IsNullOrWhiteSpace(configuredHost))
            return Describe(configuredHost.Trim(), "Docker:Host");

        if (!string.IsNullOrWhiteSpace(dockerHostEnv))
            return Describe(dockerHostEnv.Trim(), "DOCKER_HOST");

        foreach (var candidate in CandidateSockets(xdgRuntimeDir, uid))
        {
            if (socketExists(candidate))
                return Describe("unix://" + candidate, "autodetected");
        }

        return Describe(FallbackUri, "default");
    }

    /// <summary>
    /// Well-known socket paths, most likely first. Running as root, the rootful sockets are the ones
    /// that can actually be opened; running as any other uid, the per-user (rootless) sockets are
    /// tried first because the rootful ones are typically mode 0660 root:docker / root:root.
    /// </summary>
    internal static IEnumerable<string> CandidateSockets(string? xdgRuntimeDir, uint uid)
    {
        var runtimeDir = string.IsNullOrWhiteSpace(xdgRuntimeDir) ? $"/run/user/{uid}" : xdgRuntimeDir.TrimEnd('/');

        var rootful = new[] { DockerSocket, RootfulPodmanSocket };
        var rootless = new[] { $"{runtimeDir}/podman/podman.sock", $"{runtimeDir}/docker.sock" };

        return uid == 0 ? rootful.Concat(rootless) : rootless.Concat(rootful);
    }

    private static ContainerRuntimeEndpoint Describe(string uri, string source) =>
        new(uri, Classify(uri), source);

    /// <summary>
    /// Best-effort engine identification from the endpoint alone. Nothing branches on the result —
    /// see the type remarks — so a wrong guess on a custom path costs a log line, nothing more.
    /// </summary>
    internal static ContainerRuntimeKind Classify(string uri)
    {
        if (uri.Contains("podman", StringComparison.OrdinalIgnoreCase))
            return ContainerRuntimeKind.Podman;

        if (uri.Contains("docker", StringComparison.OrdinalIgnoreCase))
            return ContainerRuntimeKind.Docker;

        return ContainerRuntimeKind.Unknown;
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint NativeGetUid();

    private static uint CurrentUid()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return 0;

        try
        {
            return NativeGetUid();
        }
        catch (Exception)
        {
            // No libc (or a trimmed//restricted runtime): fall back to the root ordering, which only
            // affects which well-known socket is probed first.
            return 0;
        }
    }
}
