using System.Net;
using System.Net.Http.Json;
using AgentHost.Api.Contracts;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// Program.cs configures a chained global fixed-window limiter: 120 requests/minute per identity
/// (falls back to client IP for anonymous callers) for everything, plus a stricter 20/minute bucket
/// on /api/auth/* — the brute-force surface. QueueLimit is 0 on both (over-limit requests are
/// rejected immediately, not queued) — see the AddRateLimiter block in Program.cs.
///
/// UseRateLimiter runs BEFORE UseAuthentication/UseAuthorization, so unauthenticated floods against
/// protected endpoints are limited too (they used to be 401'd by the authorization middleware
/// before the limiter ever saw them). This test fires at POST /api/auth/login, which is both
/// AllowAnonymous and covered by the stricter auth bucket.
///
/// This intentionally does NOT share <see cref="IntegrationCollection"/>'s factory: it owns a
/// private <see cref="RateLimitedApiFactory"/> instance so tripping the limiter here can never
/// cause spurious 429s in any other test class. That factory is also the only one that leaves the
/// global limiter installed — the shared collection's host removes it, because under TestServer
/// every request lands in the same partition and the suite as a whole would otherwise trip it.
/// PermitLimit/Window are hardcoded literals in Program.cs (not configuration-driven), so a
/// fixture-only override isn't available without changing production code; instead this fires
/// enough concurrent requests to comfortably exceed 120 within the current one-minute window.
/// </summary>
public class RateLimitingTests : IClassFixture<RateLimitedApiFactory>
{
    private readonly RateLimitedApiFactory _factory;

    public RateLimitingTests(RateLimitedApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ExceedingPermitLimit_Eventually429s()
    {
        var client = _factory.CreateClient();

        // Anonymous requests all partition under the same fake TestServer client IP, so firing
        // comfortably more than PermitLimit (120) of them concurrently must trip the limiter.
        // The credentials are deliberately bogus — only the HTTP status code matters here, not
        // whether the login itself succeeds.
        const int requestCount = 150;
        var tasks = new Task<HttpStatusCode>[requestCount];
        for (var i = 0; i < requestCount; i++)
        {
            tasks[i] = SendAsync(client);
        }

        var statusCodes = await Task.WhenAll(tasks);

        Assert.Contains(statusCodes, s => s == HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpStatusCode> SendAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "rate-limit-probe@example.com",
            Password = "irrelevant",
        });
        return response.StatusCode;
    }
}
