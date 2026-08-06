using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// An <see cref="AgentHostApiFactory"/> that additionally serves the very same application over a
/// real TCP socket.
///
/// <para><b>Why.</b> <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{TEntryPoint}"/>
/// hosts the app on <c>TestServer</c>, which has no listening socket: requests are handed to the
/// pipeline in-process. That is fine for a test that speaks HTTP itself, and useless for an agent
/// *container*, which is a separate process in a separate network namespace and can only reach the
/// API over the network. This factory therefore builds the host twice from the same builder — once
/// with TestServer (returned to the base class, which requires that concrete type, and used by the
/// tests' own control-plane calls) and once with Kestrel bound to <c>0.0.0.0</c> on an ephemeral
/// port, which is the address the container calls back on. It is the arrangement documented for
/// WebApplicationFactory + a real browser/agent process; see
/// https://github.com/dotnet/aspnetcore/issues/33846.</para>
///
/// <para>Both hosts read the same configuration and the same Postgres database, so state created
/// through one is visible through the other. Binding <c>0.0.0.0</c> rather than <c>localhost</c> is
/// essential: a container reaches the host through the bridge gateway address, and a loopback-only
/// listener is unreachable from it.</para>
/// </summary>
public class KestrelAgentHostApiFactory : AgentHostApiFactory
{
    private IHost? _kestrelHost;

    /// <param name="workspaceRoot">
    /// Value for <c>Docker:WorkspacePath</c> — the directory the backend creates each run's
    /// <c>workspace/</c> and <c>secrets/</c> under, and therefore the bind-mount source the daemon
    /// sees. The production default (<c>/var/agenthost/runs</c>) is not writable by a test user, so
    /// tests point it at a temporary directory they own and can inspect afterwards.
    /// </param>
    public KestrelAgentHostApiFactory(string workspaceRoot) => WorkspaceRoot = workspaceRoot;

    public string WorkspaceRoot { get; }

    /// <summary>Base address of the real listener, e.g. <c>http://0.0.0.0:34567</c>. Its port is what a container dials.</summary>
    public Uri ListeningAddress { get; private set; } = default!;

    public int ListeningPort => ListeningAddress.Port;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.UseSetting("Docker:WorkspacePath", WorkspaceRoot);

        // The janitor's first sweep is 30s after startup and its grace periods are far longer than
        // any test, so it cannot reach this run's data; pushing the interval out anyway keeps the
        // test's filesystem assertions about *the orchestrator's* cleanup, with no second deleter
        // in the picture.
        builder.UseSetting("Retention:SweepIntervalMinutes", "600");
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Build the TestServer host FIRST, before the builder is reconfigured for Kestrel — the
        // base class casts the returned host's IServer to TestServer.
        var testHost = builder.Build();

        // 0.0.0.0:0 — every interface (so the docker bridge gateway is one of them), on a free
        // port picked by the OS (so parallel jobs never collide).
        builder.ConfigureWebHost(webHostBuilder => webHostBuilder.UseKestrel().UseUrls("http://0.0.0.0:0"));

        // Start the real server before the test server: with the deferred host builder used for
        // minimal-hosting apps, the address feature is only populated once the host is started.
        _kestrelHost = builder.Build();
        _kestrelHost.Start();

        var addresses = _kestrelHost.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel host exposed no IServerAddressesFeature");
        var address = addresses.Addresses.LastOrDefault()
            ?? throw new InvalidOperationException("Kestrel host is not listening on any address");
        ListeningAddress = new Uri(address);

        testHost.Start();
        return testHost;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _kestrelHost?.Dispose();
            _kestrelHost = null;
        }

        base.Dispose(disposing);
    }
}
