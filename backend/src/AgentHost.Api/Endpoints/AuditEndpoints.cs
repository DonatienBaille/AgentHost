using AgentHost.Api.Services;

namespace AgentHost.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organizations/{orgId}/audit-log", ListAuditLog).WithTags("Audit").WithName("ListAuditLog");
        return app;
    }

    private static async Task<IResult> ListAuditLog(string orgId, IAuditService auditService, CancellationToken ct, int skip = 0, int take = 100)
    {
        var entries = await auditService.ListByOrgAsync(orgId, skip, take, ct);
        return Results.Ok(entries);
    }
}
