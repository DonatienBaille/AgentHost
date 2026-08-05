using System.Security.Cryptography;
using System.Text;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// The one place opaque bearer-style secrets (refresh tokens, invitation tokens, password-reset
/// tokens, MFA recovery codes) are minted and hashed.
///
/// The rule every caller follows: the raw value is handed to its recipient exactly once, and only
/// <see cref="Hash"/> of it is ever persisted or compared. A database disclosure therefore yields
/// no usable credential, and lookups are done by hash so the raw value never has to be stored to
/// find the record it belongs to.
/// </summary>
public static class OpaqueToken
{
    /// <summary>Default entropy, in bytes, for a minted token. 256 bits — brute force is not a threat model.</summary>
    public const int DefaultEntropyBytes = 32;

    /// <summary>
    /// Mints a URL-safe base64 token from CSPRNG bytes, so it survives headers, query strings and
    /// JSON without escaping.
    /// </summary>
    public static string New(int entropyBytes = DefaultEntropyBytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(entropyBytes))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// SHA-256 hex of a raw token. Deliberately a plain fast hash rather than PBKDF2: unlike a
    /// user-chosen password these values carry full 256-bit entropy, so there is nothing for an
    /// offline attacker to guess and a slow KDF would only cost the request path.
    /// </summary>
    public static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
}
