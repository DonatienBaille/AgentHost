using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AgentHost.Api.Domain;
using Microsoft.IdentityModel.Tokens;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Mints and validates the short-lived token handed out when a password is correct but MFA is still
/// outstanding. Its only authority is "this user proved their password at time T"; it authorizes
/// exactly one operation, POST /api/auth/mfa/verify, and nothing else.
///
/// <b>Why it cannot be used as an access token.</b> Three independent things have to hold, and any
/// one of them alone would be enough:
/// <list type="number">
/// <item>a distinct audience (<see cref="MfaAudience"/>). The application's JWT bearer scheme is
/// configured with <c>ValidateAudience = true</c> and <c>ValidAudience = Jwt:Audience</c>
/// ("agenthost"), so a challenge token presented as <c>Authorization: Bearer</c> fails audience
/// validation and the request is 401 — the same way an agent run token is rejected (see
/// <see cref="RunTokenService"/>, which uses the same separation);</item>
/// <item>a <c>token_type</c> claim of <see cref="TokenTypeValue"/>, which no access token carries
/// and which <see cref="ValidateAndGetUserId"/> demands;</item>
/// <item>no <c>role</c> claim, so even if it somehow authenticated it would satisfy no role
/// policy.</item>
/// </list>
/// The converse also holds: a real access token fails <see cref="ValidateAndGetUserId"/>, because
/// its audience is "agenthost" and it has no <c>token_type</c> claim. Neither token can stand in
/// for the other. <c>MfaLoginTests</c> asserts both directions — an MFA feature whose challenge
/// token doubles as a session token is theatre.
/// </summary>
public interface IMfaChallengeTokenService
{
    /// <summary>Issues a challenge token binding this login attempt to one user.</summary>
    string Issue(User user);

    /// <summary>
    /// Returns the user id a challenge token is bound to, or null when the token is missing,
    /// malformed, expired, signed with the wrong key, of the wrong audience, or not a challenge
    /// token at all (e.g. an access token).
    /// </summary>
    string? ValidateAndGetUserId(string? token);

    /// <summary>Lifetime of the tokens this service issues, in seconds.</summary>
    int LifetimeSeconds { get; }
}

public class MfaChallengeTokenService : IMfaChallengeTokenService
{
    /// <summary>Audience of MFA challenge tokens — deliberately not the user-token audience.</summary>
    public const string MfaAudience = "agenthost-mfa";

    public const string TokenTypeClaim = "token_type";
    public const string TokenTypeValue = "mfa_challenge";

    /// <summary>
    /// Five minutes: long enough to fetch a code from a phone, short enough that a challenge token
    /// captured in a log or a proxy is worthless by the time anyone looks at it.
    /// </summary>
    public const int DefaultLifetimeSeconds = 300;

    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly int _lifetimeSeconds;

    public MfaChallengeTokenService(IConfiguration config)
    {
        var secret = config["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Jwt:Secret must be configured and at least 32 bytes long");

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        _issuer = config["Jwt:Issuer"] ?? "agenthost";
        _lifetimeSeconds = int.TryParse(config["Jwt:MfaChallengeSeconds"], out var s) && s > 0
            ? s
            : DefaultLifetimeSeconds;
    }

    public int LifetimeSeconds => _lifetimeSeconds;

    public string Issue(User user)
    {
        // Note what is NOT here: no role claim, and no org_id used for authorization. This token is
        // an identity assertion for one endpoint, not a session.
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Jti, UlidGenerator.NewUlid()),
            new Claim(TokenTypeClaim, TokenTypeValue),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: MfaAudience,
            claims: claims,
            expires: DateTime.UtcNow.AddSeconds(_lifetimeSeconds),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string? ValidateAndGetUserId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _issuer,
            ValidateAudience = true,
            ValidAudience = MfaAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = _key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        try
        {
            // MapInboundClaims off for the same reason Program.cs turns it off on the bearer
            // scheme: otherwise the handler rewrites "sub" to the legacy NameIdentifier URI and
            // the lookup below silently finds nothing.
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, parameters, out _);

            // Belt and braces alongside the audience check: a token without the marker claim is
            // not a challenge token, whatever else it is.
            if (principal.FindFirst(TokenTypeClaim)?.Value != TokenTypeValue)
                return null;

            return principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        }
        catch (Exception)
        {
            // Expired, wrong audience, bad signature, not a JWT at all — all the same answer, so
            // this cannot be used to probe why a token failed.
            return null;
        }
    }
}
