using System.Text;
using AgentHost.Api.Infrastructure;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// The whole point of implementing a published standard rather than inventing one is that it can be
/// checked against the specification's own numbers. These are the test vectors printed in
/// RFC 4226 (HOTP, Appendix D) and RFC 6238 (TOTP, Appendix B), verbatim.
/// </summary>
public class TotpTests
{
    /// <summary>The seed both RFCs use: the ASCII string "12345678901234567890" (20 bytes).</summary>
    private static byte[] RfcSecret => Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>RFC 4226 Appendix D — HOTP values for counters 0..9 at 6 digits.</summary>
    [Theory]
    [InlineData(0, "755224")]
    [InlineData(1, "287082")]
    [InlineData(2, "359152")]
    [InlineData(3, "969429")]
    [InlineData(4, "338314")]
    [InlineData(5, "254676")]
    [InlineData(6, "287922")]
    [InlineData(7, "162583")]
    [InlineData(8, "399871")]
    [InlineData(9, "520489")]
    public void ComputeCode_MatchesTheRfc4226HotpVectors(long counter, string expected)
    {
        Assert.Equal(expected, Totp.ComputeCode(RfcSecret, counter));
    }

    /// <summary>
    /// RFC 6238 Appendix B — the SHA-1 rows of the TOTP test table, which are given at 8 digits.
    /// The RFC's own T values are seconds since the Unix epoch with T0 = 0 and a 30s step.
    /// </summary>
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void ComputeCodeAt_MatchesTheRfc6238TotpVectors(long unixSeconds, string expected)
    {
        var time = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        Assert.Equal(expected, Totp.ComputeCodeAt(RfcSecret, time, digits: 8));
    }

    [Fact]
    public void CounterAt_IsFloorOfUnixSecondsOverTheStep()
    {
        Assert.Equal(1, Totp.CounterAt(DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal(2, Totp.CounterAt(DateTimeOffset.FromUnixTimeSeconds(60)));
        Assert.Equal(37037036, Totp.CounterAt(DateTimeOffset.FromUnixTimeSeconds(1111111109)));
    }

    [Fact]
    public void VerifyCode_AcceptsTheCurrentStepAndOneStepOfDriftEitherSide()
    {
        var secret = RfcSecret;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = TimeSpan.FromSeconds(Totp.DefaultStepSeconds);

        Assert.True(Totp.VerifyCode(secret, Totp.ComputeCodeAt(secret, now), now));
        Assert.True(Totp.VerifyCode(secret, Totp.ComputeCodeAt(secret, now - step), now));
        Assert.True(Totp.VerifyCode(secret, Totp.ComputeCodeAt(secret, now + step), now));

        // Two steps out is outside the window.
        Assert.False(Totp.VerifyCode(secret, Totp.ComputeCodeAt(secret, now - 2 * step), now));
        Assert.False(Totp.VerifyCode(secret, Totp.ComputeCodeAt(secret, now + 2 * step), now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]      // too short
    [InlineData("1234567")]    // too long
    [InlineData("abcdef")]     // not digits
    [InlineData("000000")]     // wrong (vanishingly unlikely to be the real code for this instant)
    public void VerifyCode_RejectsMalformedOrWrongCodes(string? code)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        Assert.False(Totp.VerifyCode(RfcSecret, code, now));
    }

    [Fact]
    public void VerifyCode_RejectsAValidCodeForADifferentSecret()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var otherSecret = Encoding.ASCII.GetBytes("09876543210987654321");

        Assert.False(Totp.VerifyCode(RfcSecret, Totp.ComputeCodeAt(otherSecret, now), now));
    }

    /// <summary>RFC 4648 §10 base32 vectors, minus the padding this implementation omits.</summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_MatchesTheRfc4648Vectors(string plain, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(plain)));
        Assert.Equal(plain, Encoding.ASCII.GetString(Base32.Decode(encoded)));
    }

    [Fact]
    public void Base32_RoundTripsRandomSecrets_AndToleratesLowercaseAndSpacing()
    {
        var secret = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Totp.SecretBytes);
        var encoded = Base32.Encode(secret);

        Assert.Equal(secret, Base32.Decode(encoded));
        Assert.Equal(secret, Base32.Decode(encoded.ToLowerInvariant()));
        Assert.Equal(secret, Base32.Decode(string.Join(' ', Chunk(encoded, 4))));
    }

    [Fact]
    public void Base32_Decode_RejectsCharactersOutsideTheAlphabet()
    {
        Assert.Throws<FormatException>(() => Base32.Decode("MZXW6!!!"));
    }

    [Fact]
    public void BuildOtpAuthUri_CarriesTheParametersAuthenticatorAppsNeed()
    {
        var uri = Totp.BuildOtpAuthUri("Agent Host", "user@example.com", "JBSWY3DPEHPK3PXP");

        Assert.StartsWith("otpauth://totp/", uri);
        Assert.Contains("secret=JBSWY3DPEHPK3PXP", uri);
        Assert.Contains("issuer=Agent%20Host", uri);
        Assert.Contains("algorithm=SHA1", uri);
        Assert.Contains("digits=6", uri);
        Assert.Contains("period=30", uri);
    }

    private static IEnumerable<string> Chunk(string value, int size)
    {
        for (var i = 0; i < value.Length; i += size)
            yield return value.Substring(i, Math.Min(size, value.Length - i));
    }
}
