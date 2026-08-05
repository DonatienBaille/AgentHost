using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AgentHost.Api.Domain;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Mints the short-lived, run-scoped JWT that an agent container uses to call back into the host
/// (<c>/api/agent/...</c>, see docs/agent-protocol.md). The token is injected into the container as
/// <c>AGENTHOST_RUN_TOKEN</c>.
///
/// It is deliberately *not* a user token: it is signed with the same <c>Jwt:Secret</c> but carries a
/// distinct audience (<see cref="AgentAudience"/>), so a run token is rejected by the normal user
/// bearer scheme and a user token is rejected by the agent scheme. Its only authority is the
/// <see cref="RunIdClaim"/> claim, and every agent endpoint checks that claim against the run id in
/// the route — an agent can therefore only ever affect its own run.
/// </summary>
public interface IRunTokenService
{
    /// <summary>Issues a token scoped to <paramref name="run"/>, expiring at the run's max duration plus a margin.</summary>
    string Issue(Run run);

    /// <summary>Issues a token scoped to a run id, expiring after <paramref name="maxDurationSeconds"/> plus a margin.</summary>
    string Issue(string runId, string projectId, string orgId, int maxDurationSeconds);
}

public class RunTokenService : IRunTokenService
{
    /// <summary>Authentication scheme name for the agent callback protocol.</summary>
    public const string AgentRunScheme = "AgentRun";

    /// <summary>Authorization policy requiring a valid, run-scoped agent token.</summary>
    public const string AgentRunPolicy = "RequireAgentRun";

    /// <summary>Audience of run tokens — distinct from the user audience so the two never interchange.</summary>
    public const string AgentAudience = "agenthost-agent";

    public const string RunIdClaim = "run_id";
    public const string ProjectIdClaim = "project_id";
    public const string TokenTypeClaim = "token_type";
    public const string TokenTypeValue = "agent_run";

    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly int _marginSeconds;

    public RunTokenService(IConfiguration config)
    {
        var secret = config["Jwt:Secret"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Jwt:Secret must be configured and at least 32 bytes long");

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        _issuer = config["Jwt:Issuer"] ?? "agenthost";
        _marginSeconds = int.TryParse(config["Jwt:RunTokenMarginSeconds"], out var m) ? m : 300;
    }

    public string Issue(Run run) =>
        Issue(run.Id, run.ProjectId, run.OrgId, run.RuntimeProfile.MaxDurationSeconds);

    public string Issue(string runId, string projectId, string orgId, int maxDurationSeconds)
    {
        // The token must outlive the run itself so a winding-down agent can still report its final
        // events/outputs, but not by much — it is the container's only credential.
        var lifetime = TimeSpan.FromSeconds(Math.Max(maxDurationSeconds, 0) + _marginSeconds);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, runId),
            new Claim(RunIdClaim, runId),
            new Claim(ProjectIdClaim, projectId),
            new Claim(ClaimsPrincipalExtensions.OrgIdClaim, orgId),
            new Claim(TokenTypeClaim, TokenTypeValue),
        };

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: AgentAudience,
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class RunTokenClaimsExtensions
{
    /// <summary>The run id a run-scoped agent token is bound to, or null for any other principal.</summary>
    public static string? GetRunId(this ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(RunTokenService.RunIdClaim);
}

/// <summary>
/// Registration helper for the agent callback protocol's authentication scheme, authorization
/// policy and token service. Kept next to <see cref="RunTokenService"/> so Program.cs needs a
/// single line for the whole feature.
/// </summary>
public static class AgentRunAuthenticationExtensions
{
    public static IServiceCollection AddAgentRunAuthentication(this IServiceCollection services, IConfiguration config)
    {
        var jwtSecret = config["Jwt:Secret"]
            ?? throw new InvalidOperationException("Jwt:Secret is not configured");
        var jwtIssuer = config["Jwt:Issuer"] ?? "agenthost";

        services.AddSingleton<IRunTokenService, RunTokenService>();

        services.AddAuthentication()
            .AddJwtBearer(RunTokenService.AgentRunScheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtIssuer,
                    // The audience is what separates an agent's run token from a human's user
                    // token: neither scheme will accept the other's audience.
                    ValidateAudience = true,
                    ValidAudience = RunTokenService.AgentAudience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    // Makes Identity.Name the run id, so the global rate limiter partitions
                    // agent traffic per run rather than lumping every container onto one bucket.
                    NameClaimType = RunTokenService.RunIdClaim,
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(RunTokenService.AgentRunPolicy, policy =>
            {
                policy.AuthenticationSchemes.Add(RunTokenService.AgentRunScheme);
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(RunTokenService.RunIdClaim);
                policy.RequireClaim(RunTokenService.TokenTypeClaim, RunTokenService.TokenTypeValue);
            });

        return services;
    }
}
