using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Time-based one-time passwords, RFC 6238 (which is RFC 4226's HOTP with a time-derived counter).
///
/// Implemented here rather than taken from a package: it is HMAC-SHA1 plus dynamic truncation,
/// about forty lines against <c>System.Security.Cryptography</c>, and the whole value of using a
/// published standard is that it can be checked against the RFC's own test vectors — which
/// <c>TotpTests</c> does, for both the RFC 4226 HOTP vectors and the RFC 6238 TOTP vectors.
///
/// Defaults are the ones every authenticator app assumes: HMAC-SHA1, 6 digits, a 30-second step,
/// and a ±1-step acceptance window. SHA-1 is not a weakness here — HMAC-SHA1's security does not
/// rest on collision resistance, and it is what Google Authenticator, 1Password, Aegis and the rest
/// actually implement.
/// </summary>
public static class Totp
{
    public const int DefaultDigits = 6;
    public const int DefaultStepSeconds = 30;

    /// <summary>
    /// How many steps either side of "now" are accepted. One step (±30s) absorbs ordinary clock
    /// skew and the time a human takes to type the code, without meaningfully widening the window
    /// an attacker can guess into.
    /// </summary>
    public const int DefaultDriftSteps = 1;

    /// <summary>Secret size for new enrollments: 160 bits, the size RFC 4226 §4 R6 recommends.</summary>
    public const int SecretBytes = 20;

    /// <summary>
    /// Computes the HOTP value for an explicit counter (RFC 4226 §5.3): HMAC-SHA1 over the
    /// big-endian counter, dynamic truncation, then modulo 10^digits, zero-padded.
    /// </summary>
    public static string ComputeCode(byte[] secret, long counter, int digits = DefaultDigits)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (digits is < 6 or > 9)
            throw new ArgumentOutOfRangeException(nameof(digits), "TOTP codes must be 6 to 9 digits");

        var counterBytes = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counterBytes);

        // Dynamic truncation: the low nibble of the last byte selects a 4-byte window, whose top
        // bit is masked off so the result is a positive 31-bit integer on every platform.
        var offset = hash[^1] & 0x0F;
        var binary =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString().PadLeft(digits, '0');
    }

    /// <summary>The RFC 6238 time step counter: floor((unix seconds - T0) / step), with T0 = 0.</summary>
    public static long CounterAt(DateTimeOffset time, int stepSeconds = DefaultStepSeconds) =>
        time.ToUnixTimeSeconds() / stepSeconds;

    /// <summary>Code valid at a point in time — the counter form with the clock already applied.</summary>
    public static string ComputeCodeAt(
        byte[] secret, DateTimeOffset time, int digits = DefaultDigits, int stepSeconds = DefaultStepSeconds) =>
        ComputeCode(secret, CounterAt(time, stepSeconds), digits);

    /// <summary>
    /// Verifies a user-supplied code against the secret, accepting <paramref name="driftSteps"/>
    /// steps either side of <paramref name="time"/>.
    ///
    /// Every candidate is compared in constant time and the loop is not short-circuited, so the
    /// time this takes does not reveal which step matched (or how close a wrong code was).
    /// </summary>
    public static bool VerifyCode(
        byte[] secret,
        string? code,
        DateTimeOffset time,
        int digits = DefaultDigits,
        int stepSeconds = DefaultStepSeconds,
        int driftSteps = DefaultDriftSteps)
    {
        if (secret is null || string.IsNullOrWhiteSpace(code)) return false;

        var candidate = code.Trim();
        if (candidate.Length != digits || !candidate.All(char.IsAsciiDigit)) return false;

        var candidateBytes = Encoding.ASCII.GetBytes(candidate);
        var counter = CounterAt(time, stepSeconds);

        var matched = false;
        for (var offset = -driftSteps; offset <= driftSteps; offset++)
        {
            var expected = Encoding.ASCII.GetBytes(ComputeCode(secret, counter + offset, digits));
            matched |= CryptographicOperations.FixedTimeEquals(candidateBytes, expected);
        }

        return matched;
    }

    /// <summary>
    /// The provisioning URI authenticator apps consume, usually via a QR code
    /// (<c>otpauth://totp/Issuer:account?secret=...&amp;issuer=Issuer&amp;...</c>).
    /// The parameters are stated explicitly rather than left to each app's defaults.
    /// </summary>
    public static string BuildOtpAuthUri(string issuer, string accountName, string base32Secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{accountName}");
        return $"otpauth://totp/{label}" +
               $"?secret={base32Secret}" +
               $"&issuer={Uri.EscapeDataString(issuer)}" +
               $"&algorithm=SHA1&digits={DefaultDigits}&period={DefaultStepSeconds}";
    }
}

/// <summary>
/// RFC 4648 base32 without padding — the encoding every authenticator app expects a TOTP secret in.
/// </summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length == 0) return string.Empty;

        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bitsLeft = 0;

        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bitsLeft - 5)) & 0x1F]);
                bitsLeft -= 5;
            }
        }

        if (bitsLeft > 0)
            builder.Append(Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);

        return builder.ToString();
    }

    /// <summary>
    /// Decodes base32, tolerating lowercase, padding and the spaces users paste in by accident.
    /// Throws <see cref="FormatException"/> on any character outside the alphabet.
    /// </summary>
    public static byte[] Decode(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);

        var bytes = new List<byte>(encoded.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;

        foreach (var c in encoded)
        {
            if (c is '=' or ' ' or '-') continue;

            var index = Alphabet.IndexOf(char.ToUpperInvariant(c));
            if (index < 0) throw new FormatException($"'{c}' is not a valid base32 character");

            buffer = (buffer << 5) | index;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        return bytes.ToArray();
    }
}
