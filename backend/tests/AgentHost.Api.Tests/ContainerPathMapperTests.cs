using AgentHost.Api.Infrastructure;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// The bind-mount source of a container is resolved by the daemon, not by the process issuing the
/// API call, so when the backend runs inside a container the two disagree. These tests pin the
/// translation down: it is pure string/path work and needs no daemon.
/// </summary>
public class ContainerPathMapperTests
{
    [Fact]
    public void WithoutHostPath_NothingIsRemapped()
    {
        var mapper = new ContainerPathMapper("/var/agenthost/runs");

        Assert.False(mapper.RemapsPaths);
        Assert.Equal("/var/agenthost/runs", mapper.DaemonWorkspaceRoot);
        Assert.Equal("/var/agenthost/runs/01ABC/workspace", mapper.ToDaemonPath("/var/agenthost/runs/01ABC/workspace"));
        Assert.Equal("/var/agenthost/runs/01ABC/workspace:/workspace", mapper.BindSpec("/var/agenthost/runs/01ABC/workspace", "/workspace"));
    }

    [Fact]
    public void HostPathEqualToLocalPath_IsNotTreatedAsRemapping()
    {
        var mapper = new ContainerPathMapper("/var/agenthost/runs", "/var/agenthost/runs/");

        Assert.False(mapper.RemapsPaths);
    }

    [Fact]
    public void WithHostPath_BindSourceIsTheDaemonPath_WhileLocalPathsStayLocal()
    {
        // The compose case: the backend writes to /var/agenthost/runs inside its own container,
        // the daemon must mount /srv/agenthost/runs from the host.
        var mapper = new ContainerPathMapper("/var/agenthost/runs", "/srv/agenthost/runs");

        Assert.True(mapper.RemapsPaths);
        Assert.Equal("/var/agenthost/runs/01ABC/workspace", mapper.LocalWorkspaceDirectory("01ABC"));
        Assert.Equal("/var/agenthost/runs/01ABC/secrets", mapper.LocalSecretsDirectory("01ABC"));

        Assert.Equal(
            "/srv/agenthost/runs/01ABC/workspace:/workspace",
            mapper.BindSpec(mapper.LocalWorkspaceDirectory("01ABC"), "/workspace"));
        Assert.Equal(
            "/srv/agenthost/runs/01ABC/secrets:/run/secrets:ro",
            mapper.BindSpec(mapper.LocalSecretsDirectory("01ABC"), "/run/secrets", "ro"));
    }

    [Theory]
    [InlineData("/var/agenthost/runs/", "/srv/runs/", "/var/agenthost/runs/a/b", "/srv/runs/a/b")]
    [InlineData("/var/agenthost/runs", "/srv/runs", "/var/agenthost/runs", "/srv/runs")]
    [InlineData("/var/agenthost/runs", "/", "/var/agenthost/runs/a", "/a")]
    public void TrailingSeparatorsAndRootPathsAreHandled(string local, string daemon, string input, string expected)
    {
        Assert.Equal(expected, new ContainerPathMapper(local, daemon).ToDaemonPath(input));
    }

    [Fact]
    public void WindowsStyleDaemonPath_KeepsItsOwnSeparator()
    {
        // A Linux backend can legitimately drive a daemon whose filesystem is Windows-shaped; the
        // daemon path must not be normalized against the local platform's rules.
        var mapper = new ContainerPathMapper("/var/agenthost/runs", @"C:\agenthost\runs");

        Assert.Equal(@"C:\agenthost\runs\01ABC\workspace", mapper.ToDaemonPath("/var/agenthost/runs/01ABC/workspace"));
    }

    [Fact]
    public void PathOutsideTheWorkspaceRoot_IsRejectedRatherThanGuessed()
    {
        var mapper = new ContainerPathMapper("/var/agenthost/runs", "/srv/runs");

        // A wrong bind source is silent: the daemon just creates an empty directory and the agent
        // starts with an empty /workspace and an empty /run/secrets. Fail loudly instead.
        Assert.Throws<ArgumentException>(() => mapper.ToDaemonPath("/etc/passwd"));
        Assert.Throws<ArgumentException>(() => mapper.ToDaemonPath("/var/agenthost/runs-other/x"));
        Assert.Throws<ArgumentException>(() => mapper.ToDaemonPath("/var/agenthost/runs/../../etc/passwd"));
    }

    [Fact]
    public void RelativeDaemonPath_IsRejected()
    {
        // Docker interprets a relative bind source as a NAMED VOLUME, which would mount something
        // entirely unrelated without any error.
        Assert.Throws<ArgumentException>(() => new ContainerPathMapper("/var/agenthost/runs", "runs"));
        Assert.Throws<ArgumentException>(() => new ContainerPathMapper("/var/agenthost/runs", "./runs"));
    }

    [Fact]
    public void ExtraBindOptions_AreAppendedToEveryBind_WithoutDuplicating()
    {
        var mapper = new ContainerPathMapper("/var/agenthost/runs", "/srv/runs", new[] { "z", "U" });

        Assert.Equal("/srv/runs/a:/workspace:z,U", mapper.BindSpec("/var/agenthost/runs/a", "/workspace"));
        Assert.Equal("/srv/runs/a:/run/secrets:ro,z,U", mapper.BindSpec("/var/agenthost/runs/a", "/run/secrets", "ro"));

        var withRo = new ContainerPathMapper("/var/agenthost/runs", null, new[] { "ro" });
        Assert.Equal("/var/agenthost/runs/a:/run/secrets:ro", withRo.BindSpec("/var/agenthost/runs/a", "/run/secrets", "ro"));
    }

    [Fact]
    public void FromConfiguration_ReadsAllThreeKeys_AndDefaultsTheWorkspaceRoot()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Docker:HostWorkspacePath"] = "/srv/runs",
            ["Docker:BindMountOptions"] = " z , U ",
        }).Build();

        var mapper = ContainerPathMapper.FromConfiguration(config);

        Assert.Equal(ContainerPathMapper.DefaultWorkspacePath, mapper.LocalWorkspaceRoot);
        Assert.Equal("/srv/runs", mapper.DaemonWorkspaceRoot);
        Assert.Equal(new[] { "z", "U" }, mapper.ExtraBindOptions);
    }
}
