using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Reads the authenticated caller's identity out of a <see cref="ClaimsPrincipal"/>.
///
/// These are the primitives every authorization decision must be built on: the caller's
/// organization comes from the signed token, never from a route/query/body parameter. Anything
/// that accepts an orgId/userId from the request and trusts it is a tenant-isolation hole.
///
/// SignalR hubs read <c>Context.User</c> directly through these extensions; HTTP handlers can
/// take an <see cref="ICallerContext"/> parameter instead, which is the same data with
/// non-nullable accessors.
/// </summary>
public static class ClaimsPrincipalExtensions
{
    public const string OrgIdClaim = "org_id";

    /// <summary>The caller's user id (JWT <c>sub</c>), or null when unauthenticated.</summary>
    public static string? GetUserId(this ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);

    /// <summary>The caller's organization id (JWT <c>org_id</c>), or null when unauthenticated.</summary>
    public static string? GetOrgId(this ClaimsPrincipal? principal) =>
        principal?.FindFirstValue(OrgIdClaim);

    /// <summary>The caller's role, or null when unauthenticated / role claim missing or unrecognized.</summary>
    public static UserRole? GetRole(this ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirstValue(ClaimTypes.Role);
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            return UserRoleExtensions.FromDbString(raw);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>
/// Scoped view of the current caller. Inject this into endpoint handlers (Minimal APIs resolve it
/// from DI) rather than accepting an org/user id from the client.
///
/// For HTTP requests the principal comes from the ambient <see cref="IHttpContextAccessor"/>. That
/// accessor is null during a SignalR hub method invocation — the HttpContext only exists for the
/// initial negotiate request — so hub invocations bind their principal explicitly through
/// <see cref="ICallerContextBinder"/>; see Hubs/CallerContextHubFilter.cs.
/// </summary>
public interface ICallerContext
{
    bool IsAuthenticated { get; }

    /// <summary>The caller's user id. Throws when the request is not authenticated.</summary>
    string UserId { get; }

    /// <summary>The caller's organization id. Throws when the request is not authenticated.</summary>
    string OrgId { get; }

    /// <summary>The caller's role. Throws when the request is not authenticated.</summary>
    UserRole Role { get; }

    /// <summary>True when the caller's role is at least <paramref name="minimum"/> in the owner &gt; maintainer &gt; developer &gt; viewer hierarchy.</summary>
    bool HasAtLeast(UserRole minimum);

    /// <summary>True when <paramref name="orgId"/> is the caller's own organization.</summary>
    bool BelongsToCallerOrg(string? orgId);
}

/// <summary>
/// Binds the caller for entry points that have no ambient <see cref="IHttpContextAccessor"/> —
/// today that means SignalR hub invocations. Resolve it from the *same* DI scope as the
/// <see cref="ICallerContext"/> it is meant to populate.
/// </summary>
public interface ICallerContextBinder
{
    void Bind(ClaimsPrincipal? principal);
}

public class CallerContext : ICallerContext, ICallerContextBinder
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private ClaimsPrincipal? _boundPrincipal;

    public CallerContext(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    // Resolved per access rather than captured in the constructor: a hub filter binds the principal
    // *after* this scope's services are constructed, and HttpContext is null on that path anyway.
    private ClaimsPrincipal? Principal => _boundPrincipal ?? _httpContextAccessor.HttpContext?.User;

    public void Bind(ClaimsPrincipal? principal) => _boundPrincipal = principal;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public string UserId => Principal.GetUserId()
        ?? throw new InvalidOperationException("No authenticated caller on this request");

    public string OrgId => Principal.GetOrgId()
        ?? throw new InvalidOperationException("No authenticated caller on this request");

    public UserRole Role => Principal.GetRole()
        ?? throw new InvalidOperationException("No authenticated caller on this request");

    // Lower enum value = higher privilege (Owner = 0 … Viewer = 3), matching the spec's
    // owner > maintainer > developer > viewer hierarchy.
    public bool HasAtLeast(UserRole minimum) => IsAuthenticated && Role <= minimum;

    public bool BelongsToCallerOrg(string? orgId) =>
        IsAuthenticated && !string.IsNullOrEmpty(orgId) && string.Equals(orgId, OrgId, StringComparison.Ordinal);
}
