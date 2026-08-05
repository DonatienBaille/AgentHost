using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

public static class RunEndpoints
{
    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var runsApi = app.MapGroup("/api/runs").WithTags("Runs").RequireAuthorization();

        runsApi.MapPost("/", CreateRun).WithName("CreateRun").WithValidation<CreateRunRequest>()
            .RequireAuthorization(AuthorizationPolicies.Developer);
        runsApi.MapGet("/{id}", GetRun).WithName("GetRun");
        runsApi.MapGet("/", ListRuns).WithName("ListRuns");
        runsApi.MapPost("/{id}/approve", ApproveRun).WithName("ApproveRun").WithValidation<ApprovalRequest>()
            .RequireAuthorization(AuthorizationPolicies.Developer);
        runsApi.MapPost("/{id}/answer", AnswerQuestion).WithName("AnswerQuestion").WithValidation<AnswerQuestionRequest>()
            .RequireAuthorization(AuthorizationPolicies.Developer);
        runsApi.MapPost("/{id}/cancel", CancelRun).WithName("CancelRun")
            .RequireAuthorization(AuthorizationPolicies.Developer);
        runsApi.MapGet("/{id}/events", GetRunEvents).WithName("GetRunEvents");
        runsApi.MapGet("/{id}/logs", GetRunLogs).WithName("GetRunLogs");

        return app;
    }

    private static async Task<IResult> CreateRun(CreateRunRequest req, IRunService runService, CancellationToken ct)
    {
        try
        {
            var run = await runService.CreateAsync(req, ct);
            return Results.Created($"/api/runs/{run.Id}", run);
        }
        catch (KeyNotFoundException ex)
        {
            return Results.NotFound(new { error = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.UnprocessableEntity(new { error = ex.Message });
        }
    }

    private static async Task<IResult> GetRun(string id, IRunService runService, CancellationToken ct)
    {
        var run = await runService.GetAsync(id, ct);
        return run != null ? Results.Ok(run) : Results.NotFound();
    }

    private static async Task<IResult> ListRuns(
        IRunService runService,
        CancellationToken ct,
        int skip = 0,
        int take = 50,
        string? projectId = null)
    {
        var runs = projectId is null
            ? await runService.ListAsync(skip, take, ct)
            : await runService.ListByProjectAsync(projectId, skip, take, ct);
        return Results.Ok(runs);
    }

    private static async Task<IResult> ApproveRun(string id, ApprovalRequest req, IRunService runService, CancellationToken ct)
    {
        var success = await runService.ApproveAsync(id, req, ct);
        return success ? Results.Ok() : Results.BadRequest("Cannot approve this run");
    }

    private static async Task<IResult> AnswerQuestion(string id, AnswerQuestionRequest req, IRunService runService, CancellationToken ct)
    {
        var success = await runService.AnswerQuestionAsync(id, req.QuestionId, req.Answer, ct);
        return success ? Results.Ok() : Results.BadRequest("Cannot answer for this run");
    }

    private static async Task<IResult> CancelRun(string id, IRunService runService, CancellationToken ct)
    {
        var success = await runService.CancelAsync(id, ct);
        return success ? Results.Ok() : Results.BadRequest();
    }

    private static async Task<IResult> GetRunEvents(string id, IRunService runService, CancellationToken ct, long fromSeq = 0)
    {
        var events = await runService.GetEventsAsync(id, fromSeq, ct);
        return Results.Ok(events);
    }

    private static async Task<IResult> GetRunLogs(string id, IContainerOrchestrator orchestrator, CancellationToken ct)
    {
        var logs = await orchestrator.GetLogsAsync(id, ct);
        return Results.Ok(new { logs });
    }
}
