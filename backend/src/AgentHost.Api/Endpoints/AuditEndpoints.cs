using AgentHost.Api.Infrastructure;
using AgentHost.Api.Services;

namespace AgentHost.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organizations/{orgId}/audit-log", ListAuditLog).WithTags("Audit").WithName("ListAuditLog")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        return app;
    }

    /// <summary>
    /// The {orgId} in the route is kept for URL shape/compatibility but is *not* trusted: it must
    /// match the caller's own org from the JWT, otherwise this reads as a nonexistent resource.
    /// Reading another tenant's audit log would expose their user ids, resource ids and activity.
    /// </summary>
    private static async Task<IResult> ListAuditLog(
        string orgId,
        IAuditService auditService,
        ICallerContext caller,
        CancellationToken ct,
        int skip = 0,
        int take = 100)
    {
        if (!caller.BelongsToCallerOrg(orgId)) return Results.NotFound();

        var entries = await auditService.ListByOrgAsync(caller.OrgId, Paging.ClampSkip(skip), Paging.ClampTake(take), ct);
        return Results.Ok(entries);
    }
}
