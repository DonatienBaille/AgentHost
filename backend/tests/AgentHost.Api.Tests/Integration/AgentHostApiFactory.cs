using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Boots the real <c>Program</c> pipeline (JWT auth, rate limiting, startup migrations, all
/// endpoint groups) against the local/CI Postgres instance so integration tests exercise the
/// genuine HTTP layer via <see cref="WebApplicationFactory{TEntryPoint}"/> instead of calling
/// services directly.
///
/// Configuration is intentionally *not* hardcoded here beyond selecting the "Development"
/// environment: that makes the host load appsettings.Development.json exactly like `dotnet run`
/// would locally (which already points at the sandbox's local Postgres/JWT secret/encryption
/// key), while in CI the environment-variable configuration provider (which always has higher
/// precedence than appsettings.*.json) supplies the same values via the `ConnectionStrings__*` /
/// `Secrets__*` / `Jwt__*` env vars set in .github/workflows/ci.yml — so both environments reach
/// this factory with an identical effective configuration.
/// </summary>
public class AgentHostApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }
}

/// <summary>
/// Shared collection so all HTTP-level integration test classes reuse a single
/// <see cref="AgentHostApiFactory"/> (one Postgres-backed host, migrations applied once) and run
/// sequentially against it — avoiding redundant host startups and, more importantly, avoiding
/// cross-test interference on stateful singletons like the global rate limiter.
/// RateLimitingTests deliberately opts out of this collection and uses its own private factory
/// instance instead, so it can safely trip the rate limiter without affecting any other test.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationCollection : ICollectionFixture<AgentHostApiFactory>
{
    public const string Name = "Integration";
}
