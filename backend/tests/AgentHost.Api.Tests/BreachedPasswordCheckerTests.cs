using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentHost.Api.Services;
using Microsoft.Extensions.Configuration;
using Serilog;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// The Have I Been Pwned k-anonymity check, exercised entirely against a stubbed HTTP handler.
///
/// <b>No test here touches the network</b> — not only because api.pwnedpasswords.com is unlikely to
/// be reachable from a build sandbox, but because a unit test that depends on a third party's
/// availability is a flaky test. What is worth testing is ours: the hash prefix/suffix split (and
/// the promise that nothing but the prefix leaves the process), the response parsing, the match
/// decision, and above all the fail-open behaviour.
/// </summary>
public class BreachedPasswordCheckerTests
{
    /// <summary>SHA-1("password") = 5BAA61E4C9B93F3F0682250B6CF8331B7EE68FD8.</summary>
    private const string KnownPassword = "password";
    private const string KnownPrefix = "5BAA6";
    private const string KnownSuffix = "1E4C9B93F3F0682250B6CF8331B7EE68FD8";

    private static ILogger Logger => new LoggerConfiguration().CreateLogger();

    /// <summary>Handler that answers every request from a delegate, recording what it was asked for.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(_responder(request));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static BreachedPasswordChecker Build(
        HttpMessageHandler handler, bool enabled = true, Dictionary<string, string?>? extraConfig = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Auth:BreachedPasswordCheck:Enabled"] = enabled ? "true" : "false",
            ["Auth:BreachedPasswordCheck:BaseUrl"] = "https://breach-corpus.invalid",
        };
        foreach (var (key, value) in extraConfig ?? new Dictionary<string, string?>())
            settings[key] = value;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new BreachedPasswordChecker(new StubHttpClientFactory(handler), config, Logger);
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public void SplitHash_ProducesTheFirstFiveHexCharsAndTheRemainingThirtyFive()
    {
        var (prefix, suffix) = BreachedPasswordChecker.SplitHash(KnownPassword);

        Assert.Equal(KnownPrefix, prefix);
        Assert.Equal(KnownSuffix, suffix);
        Assert.Equal(5, prefix.Length);
        Assert.Equal(35, suffix.Length);

        // The two halves together are exactly the full SHA-1, i.e. nothing is invented or lost.
        var fullHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(KnownPassword)));
        Assert.Equal(fullHash, prefix + suffix);
    }

    /// <summary>
    /// The k-anonymity promise: the request carries the 5-character prefix and nothing else — not
    /// the password, not the suffix, not the full hash, anywhere in the URL or the headers.
    /// </summary>
    [Fact]
    public async Task IsBreachedAsync_SendsOnlyTheFirstFiveHexCharsOfTheHash()
    {
        var handler = new StubHandler(_ => Ok($"{KnownSuffix}:12345"));
        var checker = Build(handler);

        await checker.IsBreachedAsync(KnownPassword);

        var request = Assert.Single(handler.Requests);
        var uri = request.RequestUri!.ToString();
        Assert.Equal($"https://breach-corpus.invalid/range/{KnownPrefix}", uri);

        var wholeRequest = uri + string.Join(';', request.Headers.Select(h => $"{h.Key}={string.Join(',', h.Value)}"));
        Assert.DoesNotContain(KnownPassword, wholeRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(KnownSuffix, wholeRequest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(KnownPrefix + KnownSuffix, wholeRequest, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IsBreachedAsync_ReturnsTrue_WhenTheSuffixIsInTheRangeResponse()
    {
        var body = $"00000000000000000000000000000000001:3\r\n{KnownSuffix}:9545824\r\nFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF:1";
        var checker = Build(new StubHandler(_ => Ok(body)));

        Assert.True(await checker.IsBreachedAsync(KnownPassword));
    }

    [Fact]
    public async Task IsBreachedAsync_ReturnsFalse_WhenTheSuffixIsAbsent()
    {
        // A realistic response for the prefix: many suffixes, none of them ours.
        var body = string.Join("\r\n", Enumerable.Range(1, 50).Select(i => $"{i:D35}:{i}"));
        var checker = Build(new StubHandler(_ => Ok(body)));

        Assert.False(await checker.IsBreachedAsync(KnownPassword));
    }

    [Fact]
    public async Task IsBreachedAsync_IsANoOp_WhenDisabled()
    {
        var handler = new StubHandler(_ => Ok($"{KnownSuffix}:12345"));
        var checker = Build(handler, enabled: false);

        Assert.False(checker.Enabled);
        Assert.False(await checker.IsBreachedAsync(KnownPassword));

        // Disabled means no outbound call at all, not merely an ignored answer.
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// Fail-open is the property that matters most: an unreachable or misbehaving third party must
    /// degrade the check, never block a legitimate signup or password change.
    /// </summary>
    [Fact]
    public async Task IsBreachedAsync_FailsOpen_OnNetworkErrors()
    {
        var checker = Build(new StubHandler(_ => throw new HttpRequestException("DNS failure")));

        Assert.False(await checker.IsBreachedAsync(KnownPassword));
    }

    [Fact]
    public async Task IsBreachedAsync_FailsOpen_OnTimeout()
    {
        var checker = Build(
            new StubHandler(_ => throw new TaskCanceledException("timed out")),
            extraConfig: new Dictionary<string, string?> { ["Auth:BreachedPasswordCheck:TimeoutSeconds"] = "1" });

        Assert.False(await checker.IsBreachedAsync(KnownPassword));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task IsBreachedAsync_FailsOpen_OnNonSuccessStatuses(HttpStatusCode status)
    {
        var checker = Build(new StubHandler(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent($"{KnownSuffix}:12345"),
        }));

        Assert.False(await checker.IsBreachedAsync(KnownPassword));
    }

    [Fact]
    public async Task IsBreachedAsync_FailsOpen_OnAGarbageBody()
    {
        var checker = Build(new StubHandler(_ => Ok("<html>service unavailable</html>")));

        Assert.False(await checker.IsBreachedAsync(KnownPassword));
    }

    [Fact]
    public async Task IsBreachedAsync_HonoursTheMinimumOccurrencesThreshold()
    {
        var body = $"{KnownSuffix}:5";

        var strict = Build(
            new StubHandler(_ => Ok(body)),
            extraConfig: new Dictionary<string, string?> { ["Auth:BreachedPasswordCheck:MinimumOccurrences"] = "10" });
        Assert.False(await strict.IsBreachedAsync(KnownPassword));

        var lenient = Build(
            new StubHandler(_ => Ok(body)),
            extraConfig: new Dictionary<string, string?> { ["Auth:BreachedPasswordCheck:MinimumOccurrences"] = "5" });
        Assert.True(await lenient.IsBreachedAsync(KnownPassword));
    }

    [Theory]
    // Present, in the various shapes a real response takes.
    [InlineData("ABC:1", "ABC", 1, true)]
    [InlineData("abc:1", "ABC", 1, true)]                       // lowercase suffix
    [InlineData("XYZ:2\r\nABC:7\r\nDEF:9", "ABC", 1, true)]      // CRLF line endings
    [InlineData("XYZ:2\nABC:7\nDEF:9", "ABC", 1, true)]          // bare LF
    [InlineData("  ABC:7  ", "ABC", 1, true)]                    // surrounding whitespace
    [InlineData("ABC", "ABC", 1, true)]                          // no count at all
    [InlineData("ABC:not-a-number", "ABC", 1, true)]             // unreadable count is not evidence of safety
    // Absent.
    [InlineData("XYZ:2\r\nDEF:9", "ABC", 1, false)]
    [InlineData("", "ABC", 1, false)]
    [InlineData("ABCD:9", "ABC", 1, false)]                      // prefix of a longer suffix, not a match
    // Threshold.
    [InlineData("ABC:3", "ABC", 5, false)]
    [InlineData("ABC:5", "ABC", 5, true)]
    public void ContainsSuffix_ParsesRangeResponsesAndAppliesTheThreshold(
        string body, string suffix, int minimum, bool expected)
    {
        Assert.Equal(expected, BreachedPasswordChecker.ContainsSuffix(body, suffix, minimum));
    }
}
