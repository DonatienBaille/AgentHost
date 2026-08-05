using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Boots the real <c>Program</c> pipeline (JWT auth, startup migrations, all endpoint groups)
/// against the local/CI Postgres instance so integration tests exercise the genuine HTTP layer via
/// <see cref="WebApplicationFactory{TEntryPoint}"/> instead of calling services directly.
///
/// Configuration is intentionally *not* hardcoded here beyond selecting the "Development"
/// environment: that makes the host load appsettings.Development.json exactly like `dotnet run`
/// would locally (which already points at the sandbox's local Postgres/JWT secret/encryption
/// key), while in CI the environment-variable configuration provider (which always has higher
/// precedence than appsettings.*.json) supplies the same values via the `ConnectionStrings__*` /
/// `Secrets__*` / `Jwt__*` env vars set in .github/workflows/ci.yml — so both environments reach
/// this factory with an identical effective configuration.
///
/// The one deliberate deviation is <see cref="EnableRateLimiting"/>. Program.cs installs a global
/// 120-requests-per-minute fixed-window limiter, and under TestServer every request partitions
/// into the *same* bucket (there is no real client IP, and the JWTs carry no `name` claim), so the
/// suite as a whole trips it and tests start failing with spurious 429s in whatever order they
/// happen to run. Turning the global limiter off here keeps that shared state from coupling
/// unrelated tests together; the production limiter configuration is still exercised end-to-end by
/// <c>RateLimitingTests</c>, which uses a factory that leaves it switched on.
/// </summary>
public class AgentHostApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// When false (the default), the global rate limiter is removed from this host. Override to
    /// true in a factory whose tests are *about* rate limiting.
    /// </summary>
    protected virtual bool EnableRateLimiting => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        if (!EnableRateLimiting)
        {
            // Runs after Program.cs's own AddRateLimiter, so this configuration wins and clears
            // the global limiter. Everything else about the pipeline is left untouched.
            builder.ConfigureServices(services =>
                services.Configure<RateLimiterOptions>(options => options.GlobalLimiter = null));
        }
    }
}

/// <summary>Factory that keeps Program.cs's real global rate limiter in place.</summary>
public class RateLimitedApiFactory : AgentHostApiFactory
{
    protected override bool EnableRateLimiting => true;
}

/// <summary>
/// Shared collection so all HTTP-level integration test classes reuse a single
/// <see cref="AgentHostApiFactory"/> (one Postgres-backed host, migrations applied once) and run
/// sequentially against it — avoiding redundant host startups.
/// RateLimitingTests deliberately opts out of this collection and uses its own
/// <see cref="RateLimitedApiFactory"/> instead, so it can safely trip the rate limiter without
/// affecting any other test.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<AgentHostApiFactory>
{
    public const string Name = "Integration";
}
