using AgentHost.Api.Domain;
using Microsoft.AspNetCore.Authorization;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// RBAC policy names (spec roles: owner > maintainer > developer > viewer, each including the
/// permissions of the ones below it). Register with AddAuthorizationPolicies() in Program.cs;
/// apply to a route group/handler with .RequireAuthorization(AuthorizationPolicies.Maintainer).
/// Any authenticated user (including Viewer) is covered by the bare .RequireAuthorization()
/// default policy — no named policy is needed for read-only endpoints.
/// </summary>
public static class AuthorizationPolicies
{
    public const string Owner = "RequireOwner";
    public const string Maintainer = "RequireMaintainer";
    public const string Developer = "RequireDeveloper";

    public static IServiceCollection AddAuthorizationPolicies(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(Owner, p => p.RequireRole(UserRole.Owner.ToDbString()))
            .AddPolicy(Maintainer, p => p.RequireRole(UserRole.Owner.ToDbString(), UserRole.Maintainer.ToDbString()))
            .AddPolicy(Developer, p => p.RequireRole(
                UserRole.Owner.ToDbString(), UserRole.Maintainer.ToDbString(), UserRole.Developer.ToDbString()));

        return services;
    }
}
