using System.Security.Cryptography;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Minimal dependency-free ULID generator (https://github.com/ulid/spec).
/// Produces a 26-character Crockford base32 string: 48-bit millisecond timestamp
/// followed by 80 bits of cryptographically random entropy.
/// </summary>
public static class UlidGenerator
{
    private const string Base32Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Generates a new ULID string (26 chars, Crockford base32, lexicographically sortable).</summary>
    public static string NewUlid()
    {
        var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> randomness = stackalloc byte[10]; // 80 bits
        RandomNumberGenerator.Fill(randomness);

        Span<byte> bytes = stackalloc byte[16];
        // 48-bit big-endian timestamp
        bytes[0] = (byte)(timestampMs >> 40);
        bytes[1] = (byte)(timestampMs >> 32);
        bytes[2] = (byte)(timestampMs >> 24);
        bytes[3] = (byte)(timestampMs >> 16);
        bytes[4] = (byte)(timestampMs >> 8);
        bytes[5] = (byte)timestampMs;
        randomness.CopyTo(bytes[6..]);

        return Encode(bytes);
    }

    private static string Encode(ReadOnlySpan<byte> bytes)
    {
        // 16 bytes (128 bits) -> 26 base32 characters (130 bits, top 2 bits of first char unused)
        Span<char> result = stackalloc char[26];

        result[0] = Base32Alphabet[(bytes[0] & 0xE0) >> 5];
        result[1] = Base32Alphabet[bytes[0] & 0x1F];
        result[2] = Base32Alphabet[(bytes[1] & 0xF8) >> 3];
        result[3] = Base32Alphabet[((bytes[1] & 0x07) << 2) | ((bytes[2] & 0xC0) >> 6)];
        result[4] = Base32Alphabet[(bytes[2] & 0x3E) >> 1];
        result[5] = Base32Alphabet[((bytes[2] & 0x01) << 4) | ((bytes[3] & 0xF0) >> 4)];
        result[6] = Base32Alphabet[((bytes[3] & 0x0F) << 1) | ((bytes[4] & 0x80) >> 7)];
        result[7] = Base32Alphabet[(bytes[4] & 0x7C) >> 2];
        result[8] = Base32Alphabet[((bytes[4] & 0x03) << 3) | ((bytes[5] & 0xE0) >> 5)];
        result[9] = Base32Alphabet[bytes[5] & 0x1F];

        result[10] = Base32Alphabet[(bytes[6] & 0xF8) >> 3];
        result[11] = Base32Alphabet[((bytes[6] & 0x07) << 2) | ((bytes[7] & 0xC0) >> 6)];
        result[12] = Base32Alphabet[(bytes[7] & 0x3E) >> 1];
        result[13] = Base32Alphabet[((bytes[7] & 0x01) << 4) | ((bytes[8] & 0xF0) >> 4)];
        result[14] = Base32Alphabet[((bytes[8] & 0x0F) << 1) | ((bytes[9] & 0x80) >> 7)];
        result[15] = Base32Alphabet[(bytes[9] & 0x7C) >> 2];
        result[16] = Base32Alphabet[((bytes[9] & 0x03) << 3) | ((bytes[10] & 0xE0) >> 5)];
        result[17] = Base32Alphabet[bytes[10] & 0x1F];

        result[18] = Base32Alphabet[(bytes[11] & 0xF8) >> 3];
        result[19] = Base32Alphabet[((bytes[11] & 0x07) << 2) | ((bytes[12] & 0xC0) >> 6)];
        result[20] = Base32Alphabet[(bytes[12] & 0x3E) >> 1];
        result[21] = Base32Alphabet[((bytes[12] & 0x01) << 4) | ((bytes[13] & 0xF0) >> 4)];
        result[22] = Base32Alphabet[((bytes[13] & 0x0F) << 1) | ((bytes[14] & 0x80) >> 7)];
        result[23] = Base32Alphabet[(bytes[14] & 0x7C) >> 2];
        result[24] = Base32Alphabet[((bytes[14] & 0x03) << 3) | ((bytes[15] & 0xE0) >> 5)];
        result[25] = Base32Alphabet[bytes[15] & 0x1F];

        return new string(result);
    }
}
