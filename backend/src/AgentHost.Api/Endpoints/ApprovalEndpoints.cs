using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Read-only listing of approvals (human-in-the-loop gates/questions, spec 5.1 `approvals`
/// table). Creating/deciding approvals happens through the run-scoped endpoints
/// (`/api/runs/{id}/approve`, `/api/runs/{id}/answer`) and the RunHub SignalR methods.
///
/// Approvals carry the prompt text of another tenant's run, so both routes are org-scoped through
/// the owning run.
/// </summary>
public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var approvalsApi = app.MapGroup("/api/approvals").WithTags("Approvals").RequireAuthorization();

        approvalsApi.MapGet("/{id}", GetApproval).WithName("GetApproval");

        app.MapGet("/api/runs/{runId}/approvals", ListApprovalsForRun).WithName("ListApprovalsForRun").RequireAuthorization();

        return app;
    }

    private static async Task<IResult> GetApproval(string id, IApprovalRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var approval = await repository.GetAsync(id, caller.OrgId, ct);
        return approval != null ? Results.Ok(approval) : Results.NotFound();
    }

    private static async Task<IResult> ListApprovalsForRun(
        string runId, IApprovalRepository repository, ICallerContext caller, CancellationToken ct)
    {
        var approvals = await repository.ListByRunAsync(runId, caller.OrgId, ct);
        return Results.Ok(approvals);
    }
}
