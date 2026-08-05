using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace AgentHost.Api.Services;

public interface IBreachedPasswordChecker
{
    /// <summary>True when <c>Auth:BreachedPasswordCheck:Enabled</c> turns the check on. Off by default.</summary>
    bool Enabled { get; }

    /// <summary>
    /// True when the password appears in the configured breach corpus. <b>Fails open</b>: any
    /// network, timeout or parsing problem answers false, so a third party being unreachable can
    /// never block a legitimate signup or password change.
    /// </summary>
    Task<bool> IsBreachedAsync(string password, CancellationToken ct = default);
}

/// <summary>
/// Optional breached-password screening against Have I Been Pwned's range API, layered on top of
/// the length + blocklist rules in <see cref="Validation.PasswordPolicy"/>.
///
/// <b>k-anonymity.</b> The password never leaves the process, and neither does its full hash. Only
/// the first five hex characters of its SHA-1 are sent (<c>GET /range/{prefix}</c>); the service
/// answers with every suffix it knows for that prefix — some hundreds of them — and the comparison
/// happens locally. The remote end therefore learns a bucket that roughly a 1-in-a-million slice of
/// all passwords falls into, and nothing else. SHA-1 is not a security choice here, it is simply
/// the index HIBP publishes.
///
/// <b>Fail-open, deliberately.</b> Every failure path — DNS, TLS, timeout, 5xx, malformed body —
/// answers "not breached" and logs a warning. The alternative, failing closed, hands a third party
/// the ability to lock every user of this system out of registering or changing a password by
/// being down. The check strengthens a password policy; it is not permitted to become a dependency
/// of it.
///
/// Off by default (<c>Auth:BreachedPasswordCheck:Enabled</c>), because it introduces an outbound
/// network call on a request path that otherwise has none.
/// </summary>
public class BreachedPasswordChecker : IBreachedPasswordChecker
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client, so its handler can be configured (or stubbed) in isolation.</summary>
    public const string HttpClientName = "breached-passwords";

    public const string DefaultBaseUrl = "https://api.pwnedpasswords.com";

    /// <summary>
    /// Short by design. This runs inline on registration/password-change requests, so a slow third
    /// party must degrade into "not breached" quickly rather than making the endpoint feel broken.
    /// </summary>
    public const int DefaultTimeoutSeconds = 2;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly bool _enabled;
    private readonly string _baseUrl;
    private readonly TimeSpan _timeout;
    private readonly int _minimumOccurrences;

    public BreachedPasswordChecker(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        // Absent/unparseable => OFF. An outbound call on the signup path is something a deployment
        // opts into explicitly, never something it acquires by accident.
        _enabled = bool.TryParse(config["Auth:BreachedPasswordCheck:Enabled"], out var enabled) && enabled;
        _baseUrl = (config["Auth:BreachedPasswordCheck:BaseUrl"] ?? DefaultBaseUrl).TrimEnd('/');
        _timeout = TimeSpan.FromSeconds(
            int.TryParse(config["Auth:BreachedPasswordCheck:TimeoutSeconds"], out var s) && s > 0
                ? s
                : DefaultTimeoutSeconds);
        _minimumOccurrences =
            int.TryParse(config["Auth:BreachedPasswordCheck:MinimumOccurrences"], out var m) && m > 0 ? m : 1;
    }

    public bool Enabled => _enabled;

    public async Task<bool> IsBreachedAsync(string password, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrEmpty(password))
            return false;

        var (prefix, suffix) = SplitHash(password);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/range/{prefix}");
            // Asks the service to pad its response with random entries, so an observer cannot infer
            // the queried prefix from the response size.
            request.Headers.Add("Add-Padding", "true");

            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Warning(
                    "Breached-password check degraded: {BaseUrl} answered {StatusCode}; treating the password as not breached",
                    _baseUrl, (int)response.StatusCode);
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return ContainsSuffix(body, suffix, _minimumOccurrences);
        }
        catch (Exception ex)
        {
            // Includes OperationCanceledException from our own timeout. Even a caller-cancelled
            // request degrades to "not breached" rather than throwing out of a validator.
            _logger.Warning(ex,
                "Breached-password check failed against {BaseUrl}; failing open and treating the password as not breached",
                _baseUrl);
            return false;
        }
    }

    /// <summary>
    /// Splits a password's SHA-1 into the 5-character prefix that is sent to the service and the
    /// 35-character suffix that stays here. Uppercase hex, the encoding the range API uses.
    /// </summary>
    public static (string Prefix, string Suffix) SplitHash(string password)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));
        return (hash[..5], hash[5..]);
    }

    /// <summary>
    /// Parses a range response ("SUFFIX:COUNT" per line) and decides whether our suffix is present
    /// with at least <paramref name="minimumOccurrences"/> sightings.
    ///
    /// Tolerant on purpose: line endings vary, casing is not guaranteed, and a line without a
    /// parseable count is treated as a sighting rather than being silently dropped — an unreadable
    /// count is not evidence that the password is safe.
    /// </summary>
    public static bool ContainsSuffix(string responseBody, string suffix, int minimumOccurrences = 1)
    {
        if (string.IsNullOrEmpty(responseBody) || string.IsNullOrEmpty(suffix))
            return false;

        foreach (var rawLine in responseBody.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;

            var separator = line.IndexOf(':');
            var lineSuffix = separator >= 0 ? line[..separator] : line;

            if (!lineSuffix.Equals(suffix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (separator < 0) return true;

            var countText = line[(separator + 1)..].Trim();
            return !int.TryParse(countText, out var count) || count >= minimumOccurrences;
        }

        return false;
    }
}
