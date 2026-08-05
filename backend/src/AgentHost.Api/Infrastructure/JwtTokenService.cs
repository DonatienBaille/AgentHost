using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AgentHost.Api.Domain;
using Microsoft.IdentityModel.Tokens;

namespace AgentHost.Api.Infrastructure;

public interface IJwtTokenService
{
    string GenerateToken(User user);
}

/// <summary>
/// Issues HMAC-SHA256 signed JWTs carrying the claims RequirePolicies/RunHub etc. rely on:
/// sub (user id), org_id, email, role. Symmetric key comes from config Jwt:Secret (must be
/// at least 32 bytes) — see appsettings.Development.json / docker-compose.yml for the dev value.
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _expiryMinutes;

    public JwtTokenService(IConfiguration config)
    {
        var secret = config["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Jwt:Secret must be configured and at least 32 bytes long");

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        _issuer = config["Jwt:Issuer"] ?? "agenthost";
        _audience = config["Jwt:Audience"] ?? "agenthost";
        _expiryMinutes = int.TryParse(config["Jwt:ExpiryMinutes"], out var m) ? m : 480;
    }

    public string GenerateToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("org_id", user.OrgId),
            new Claim(ClaimTypes.Role, user.Role.ToDbString()),
        };

        var credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_expiryMinutes),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
