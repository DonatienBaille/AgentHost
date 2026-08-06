using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using AgentHost.Api.Validation;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Run endpoints. Every handler resolves the run through <see cref="IRunRepository"/> with the
/// caller's org from the JWT, so another tenant's run is indistinguishable from a nonexistent one
/// (404, never 403 — a 403 would let an attacker probe ULIDs for existence).
/// </summary>
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

    private static async Task<IResult> CreateRun(
        CreateRunRequest req,
        IRunService runService,
        IAgentRepository agentRepository,
        ICallerContext caller,
        CancellationToken ct)
    {
        // The run's org is derived downstream from the agent's project, so the agent itself is the
        // tenant boundary here: without this check any authenticated user could start runs against
        // another organization's agents (and be billed to, and read the output of, that org).
        var agent = await agentRepository.GetAsync(req.AgentId, caller.OrgId, ct);
        if (agent is null) return Results.NotFound(new { error = "Agent not found" });

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

    private static async Task<IResult> GetRun(string id, IRunRepository runRepository, ICallerContext caller, CancellationToken ct)
    {
        var run = await runRepository.GetAsync(id, caller.OrgId, ct);
        return run != null ? Results.Ok(run) : Results.NotFound();
    }

    /// <summary>
    /// Lists the caller's own organization's runs. <c>take</c>/<c>skip</c> are clamped — an
    /// unbounded page size is a trivial denial-of-service.
    /// </summary>
    private static async Task<IResult> ListRuns(
        IRunRepository runRepository,
        ICallerContext caller,
        CancellationToken ct,
        int skip = 0,
        int take = 50,
        string? projectId = null)
    {
        skip = Paging.ClampSkip(skip);
        take = Paging.ClampTake(take);

        var runs = projectId is null
            ? await runRepository.ListByOrgAsync(caller.OrgId, skip, take, ct)
            : await runRepository.ListByProjectAsync(projectId, caller.OrgId, skip, take, ct);
        return Results.Ok(runs);
    }

    private static async Task<IResult> ApproveRun(
        string id, ApprovalRequest req, IRunService runService, IRunRepository runRepository, ICallerContext caller, CancellationToken ct)
    {
        if (await runRepository.GetAsync(id, caller.OrgId, ct) is null) return Results.NotFound();

        var success = await runService.ApproveAsync(id, req, ct);
        return success ? Results.Ok() : Results.BadRequest("Cannot approve this run");
    }

    private static async Task<IResult> AnswerQuestion(
        string id, AnswerQuestionRequest req, IRunService runService, IRunRepository runRepository, ICallerContext caller, CancellationToken ct)
    {
        if (await runRepository.GetAsync(id, caller.OrgId, ct) is null) return Results.NotFound();

        var success = await runService.AnswerQuestionAsync(id, req.QuestionId, req.Answer, ct);
        return success ? Results.Ok() : Results.BadRequest("Cannot answer for this run");
    }

    /// <summary>
    /// Annule un run. <b>200</b> quand l'arrêt du conteneur est confirmé, <b>202</b> quand le run est
    /// marqué annulé sans que personne n'ait pu confirmer l'arrêt (aucun runner enregistré, runner
    /// injoignable). Le 202 n'est pas cosmétique : répondre 200 dans ce cas est précisément le
    /// mensonge silencieux que le tier runner rend courant. Le détail voyage aussi sur la
    /// chronologie du run, en événement <c>run.cancel_unconfirmed</c>.
    /// </summary>
    private static async Task<IResult> CancelRun(
        string id, IRunService runService, IRunRepository runRepository, ICallerContext caller, CancellationToken ct)
    {
        if (await runRepository.GetAsync(id, caller.OrgId, ct) is null) return Results.NotFound();

        var result = await runService.CancelAsync(id, ct);
        if (result is null) return Results.BadRequest();

        return result.ContainerStopConfirmed
            ? Results.Ok(new { cancelled = true, containerStopConfirmed = true })
            : Results.Accepted(value: new
            {
                cancelled = true,
                containerStopConfirmed = false,
                detail = result.Detail,
            });
    }

    private static async Task<IResult> GetRunEvents(
        string id,
        IRunEventRepository runEventRepository,
        IRunRepository runRepository,
        ICallerContext caller,
        CancellationToken ct,
        long fromSeq = 0,
        int take = 200)
    {
        if (await runRepository.GetAsync(id, caller.OrgId, ct) is null) return Results.NotFound();

        var events = await runEventRepository.ListByRunAsync(id, caller.OrgId, fromSeq, Paging.ClampTake(take), ct);
        return Results.Ok(events);
    }

    /// <summary>
    /// Journaux du conteneur. <b>503</b> quand ils n'ont pas pu être obtenus — aucun runner
    /// enregistré, runner injoignable, conteneur déjà supprimé.
    ///
    /// <para>Auparavant, ces cas renvoyaient 200 avec le message d'erreur <em>dans</em> le champ
    /// <c>logs</c>, où il s'affiche comme la sortie de l'agent. Un client ne pouvait pas distinguer
    /// « l'agent a écrit ceci » de « nous n'avons pas pu lire ». Le champ <c>logs</c> reste présent
    /// et vide dans la réponse d'erreur, pour ne pas casser les clients qui le lisent sans regarder
    /// le code de statut.</para>
    /// </summary>
    private static async Task<IResult> GetRunLogs(
        string id, IContainerOrchestrator orchestrator, IRunRepository runRepository, ICallerContext caller, CancellationToken ct)
    {
        if (await runRepository.GetAsync(id, caller.OrgId, ct) is null) return Results.NotFound();

        var logs = await orchestrator.GetLogsAsync(id, ct);

        return logs.Retrieved
            ? Results.Ok(new { logs = logs.Content, available = true })
            : Results.Json(
                new { logs = string.Empty, available = false, detail = logs.Detail },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}
