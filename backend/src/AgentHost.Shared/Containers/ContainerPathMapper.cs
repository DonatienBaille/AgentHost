using Microsoft.Extensions.Configuration;

namespace AgentHost.Shared.Containers;

/// <summary>
/// Translates run-data paths between the two filesystems that are involved in every container
/// launch, and builds the bind-mount specifications from the result.
///
/// <para><b>Why this exists.</b> A bind mount source is resolved by the <em>container daemon</em>
/// (dockerd / the Podman service), never by the process that issues the API call. When the backend
/// itself runs in a container, the path it writes to (<c>Docker:WorkspacePath</c>, e.g.
/// <c>/var/agenthost/runs</c> inside the backend container) is not the path the daemon must mount
/// (e.g. <c>/srv/agenthost/runs</c> on the host). Sending the backend's own path produces a bind on
/// a directory the daemon has never heard of: the daemon happily creates an empty one, so the agent
/// starts with an empty <c>/workspace</c> and — worse — an empty <c>/run/secrets</c>. That was the
/// documented "Docker-in-Docker bind mount" limitation; it is now a configuration knob.</para>
///
/// <para><b>The knob.</b> <c>Docker:HostWorkspacePath</c> is the path <em>the daemon</em> sees for
/// the very same directory tree that the backend reads and writes through
/// <c>Docker:WorkspacePath</c>. When it is unset (or equal to the local path) nothing is remapped
/// and behaviour is exactly what it was — the bare-metal case where both processes share one
/// filesystem view.</para>
///
/// <para>The daemon path is deliberately <em>not</em> normalized with <see cref="Path"/>: it names a
/// location on a foreign filesystem, possibly with a different separator convention (a Windows
/// daemon path while the backend runs on Linux), and <see cref="Path.GetFullPath(string)"/> would
/// mangle it against the local platform's rules.</para>
/// </summary>
public sealed class ContainerPathMapper
{
    public const string DefaultWorkspacePath = "/var/agenthost/runs";

    private readonly char _daemonSeparator;

    /// <summary>Root the backend process reads/writes (<c>Docker:WorkspacePath</c>), normalized.</summary>
    public string LocalWorkspaceRoot { get; }

    /// <summary>Root the daemon must mount (<c>Docker:HostWorkspacePath</c>), or the local root when unset.</summary>
    public string DaemonWorkspaceRoot { get; }

    /// <summary>True when the two roots differ, i.e. paths actually get rewritten.</summary>
    public bool RemapsPaths { get; }

    /// <summary>
    /// Extra mount options appended to every bind (<c>Docker:BindMountOptions</c>). Empty by default.
    /// Deployment-specific rather than runtime-specific: SELinux hosts want <c>z</c>, rootless Podman
    /// often wants <c>U</c> so the mounted tree is chowned into the container's user namespace. See
    /// the Podman section of README.md.
    /// </summary>
    public IReadOnlyList<string> ExtraBindOptions { get; }

    public ContainerPathMapper(string? localRoot, string? daemonRoot = null, IEnumerable<string>? extraBindOptions = null)
    {
        var local = string.IsNullOrWhiteSpace(localRoot) ? DefaultWorkspacePath : localRoot.Trim();
        LocalWorkspaceRoot = TrimTrailingSeparators(Path.GetFullPath(local));

        if (string.IsNullOrWhiteSpace(daemonRoot))
        {
            DaemonWorkspaceRoot = LocalWorkspaceRoot;
        }
        else
        {
            var daemon = TrimTrailingSeparators(daemonRoot.Trim());
            if (!IsAbsolute(daemon))
            {
                throw new ArgumentException(
                    $"Docker:HostWorkspacePath must be an absolute path as the container daemon sees it " +
                    $"(a relative source is interpreted as a named volume, silently mounting the wrong thing); got '{daemonRoot}'.",
                    nameof(daemonRoot));
            }

            DaemonWorkspaceRoot = daemon;
        }

        RemapsPaths = !string.Equals(LocalWorkspaceRoot, DaemonWorkspaceRoot, StringComparison.Ordinal);
        _daemonSeparator = DetectSeparator(DaemonWorkspaceRoot);

        ExtraBindOptions = (extraBindOptions ?? Array.Empty<string>())
            .Select(o => o?.Trim() ?? string.Empty)
            .Where(o => o.Length > 0)
            .ToArray();
    }

    public static ContainerPathMapper FromConfiguration(IConfiguration config) => new(
        config["Docker:WorkspacePath"],
        config["Docker:HostWorkspacePath"],
        SplitOptions(config["Docker:BindMountOptions"]));

    internal static IEnumerable<string> SplitOptions(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Directory the backend writes the run's workspace into.</summary>
    public string LocalWorkspaceDirectory(string runId) => Path.Combine(LocalWorkspaceRoot, runId, "workspace");

    /// <summary>Directory the backend writes the run's plaintext secrets into.</summary>
    public string LocalSecretsDirectory(string runId) => Path.Combine(LocalWorkspaceRoot, runId, "secrets");

    /// <summary>
    /// Rewrites a path under <see cref="LocalWorkspaceRoot"/> into the equivalent path under
    /// <see cref="DaemonWorkspaceRoot"/>. Throws when the path is outside the workspace root, because
    /// mapping it would be a guess — and a wrong bind source is a silent data/secret loss, not an error
    /// the daemon reports.
    /// </summary>
    public string ToDaemonPath(string localPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);

        var full = TrimTrailingSeparators(Path.GetFullPath(localPath));

        if (!IsUnderLocalRoot(full))
        {
            throw new ArgumentException(
                $"'{localPath}' is outside the workspace root '{LocalWorkspaceRoot}' and cannot be mapped to a daemon path.",
                nameof(localPath));
        }

        if (!RemapsPaths)
            return full;

        if (full.Length == LocalWorkspaceRoot.Length)
            return DaemonWorkspaceRoot;

        var relative = full[(LocalWorkspaceRoot.Length + 1)..]
            .Replace(Path.DirectorySeparatorChar, _daemonSeparator)
            .Replace(Path.AltDirectorySeparatorChar, _daemonSeparator);

        var root = DaemonWorkspaceRoot.EndsWith(_daemonSeparator) ? DaemonWorkspaceRoot : DaemonWorkspaceRoot + _daemonSeparator;
        return root + relative;
    }

    /// <summary>
    /// Builds a <c>source:destination[:options]</c> bind specification, mapping the source to the
    /// daemon's view and appending <see cref="ExtraBindOptions"/> after any caller-supplied options.
    /// </summary>
    public string BindSpec(string localPath, string containerPath, params string[] options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);

        var source = ToDaemonPath(localPath);
        var allOptions = options.Concat(ExtraBindOptions)
            .Select(o => o?.Trim() ?? string.Empty)
            .Where(o => o.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return allOptions.Length == 0
            ? $"{source}:{containerPath}"
            : $"{source}:{containerPath}:{string.Join(',', allOptions)}";
    }

    private bool IsUnderLocalRoot(string fullPath)
    {
        if (!fullPath.StartsWith(LocalWorkspaceRoot, StringComparison.Ordinal))
            return false;

        return fullPath.Length == LocalWorkspaceRoot.Length
               || fullPath[LocalWorkspaceRoot.Length] == Path.DirectorySeparatorChar
               || fullPath[LocalWorkspaceRoot.Length] == Path.AltDirectorySeparatorChar;
    }

    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        // "/" (and "C:\") trim to nothing / to a bare drive letter; keep them addressable.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? path[..(trimmed.Length + 1)] : trimmed;
    }

    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || path.StartsWith('\\') ||
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));

    private static char DetectSeparator(string root) =>
        root.Contains('\\') && !root.Contains('/') ? '\\' : '/';
}
