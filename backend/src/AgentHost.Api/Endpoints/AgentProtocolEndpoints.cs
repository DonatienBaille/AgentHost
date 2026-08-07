using System.Security.Claims;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Serilog;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// The agent → host callback protocol (v1.0), documented in docs/agent-protocol.md.
///
/// These endpoints are spoken by the *agent container*, not by a human client. They authenticate
/// with the run-scoped <c>AGENTHOST_RUN_TOKEN</c> under the dedicated
/// <see cref="RunTokenService.AgentRunScheme"/> scheme (distinct audience), so a normal user JWT is
/// never accepted here and a run token is never accepted on the user-facing API.
///
/// Every handler re-checks that the token's <c>run_id</c> claim equals the <c>{runId}</c> in the
/// route: possessing a token for run A must not let a container touch run B.
/// </summary>
public static class AgentProtocolEndpoints
{
    private const int DefaultApprovalTtlSeconds = 3600;
    private const int MaxApprovalTtlSeconds = 24 * 3600;

    public static IEndpointRouteBuilder MapAgentProtocolEndpoints(this IEndpointRouteBuilder app)
    {
        var agentApi = app.MapGroup("/api/agent")
            .WithTags("AgentProtocol")
            .RequireAuthorization(RunTokenService.AgentRunPolicy);

        agentApi.MapPost("/runs/{runId}/events", PostEvent).WithName("AgentPostEvent");
        agentApi.MapPost("/runs/{runId}/outputs", PostOutputs).WithName("AgentPostOutputs");
        agentApi.MapPost("/runs/{runId}/approvals", RequestApproval).WithName("AgentRequestApproval");
        agentApi.MapGet("/runs/{runId}/approvals/{approvalId}", GetApprovalStatus).WithName("AgentGetApprovalStatus");
        agentApi.MapPost("/runs/{runId}/questions", AskQuestion).WithName("AgentAskQuestion");
        agentApi.MapPost("/runs/{runId}/usage", ReportUsage).WithName("AgentReportUsage");

        return app;
    }

    // ---- POST /api/agent/runs/{runId}/events ----

    private static async Task<IResult> PostEvent(
        string runId,
        AgentEventRequest req,
        ClaimsPrincipal principal,
        IRunRepository runRepository,
        IEventBus eventBus,
        CancellationToken ct)
    {
        if (BindRun(runId, principal) is { } denied) return denied;

        if (string.IsNullOrWhiteSpace(req.EventType))
            return Results.BadRequest(new { error = "eventType is required" });

        var run = await runRepository.GetAsync(runId, ct);
        if (run is null) return Results.NotFound();

        var evt = new RunEvent
        {
            RunId = runId,
            EventType = req.EventType,
            Level = NormalizeLevel(req.Level),
            Message = req.Message,
            Payload = req.Payload,
        };

        // IEventBus persists to run_events (assigning seq) *and* broadcasts to the SignalR
        // group run-{runId}, which is exactly what a live run viewer subscribes to.
        await eventBus.PublishAsync(evt, ct);

        return Results.Ok(new AgentEventResponse { Seq = evt.Seq });
    }

    // ---- POST /api/agent/runs/{runId}/outputs ----

    private static async Task<IResult> PostOutputs(
        string runId,
        AgentOutputsRequest req,
        ClaimsPrincipal principal,
        IRunRepository runRepository,
        IEventBus eventBus,
        CancellationToken ct)
    {
        if (BindRun(runId, principal) is { } denied) return denied;

        if (req.Outputs is null)
            return Results.BadRequest(new { error = "outputs is required" });

        var updated = await runRepository.SetOutputsAsync(runId, req.Outputs, ct);
        if (!updated) return Results.NotFound();

        await eventBus.PublishAsync(new RunEvent
        {
            RunId = runId,
            EventType = "run.outputs_published",
            Level = "info",
            Message = "Agent published run outputs",
        }, ct);

        return Results.Ok(new { runId, outputs = req.Outputs });
    }

    // ---- POST /api/agent/runs/{runId}/approvals ----

    private static Task<IResult> RequestApproval(
        string runId,
        AgentApprovalRequest req,
        ClaimsPrincipal principal,
        IRunRepository runRepository,
        IApprovalRepository approvalRepository,
        IAgentRepository agentRepository,
        IAgentManifestParser manifestParser,
        IEventBus eventBus,
        IWebhookDispatcher webhookDispatcher,
        RunStateMachine stateMachine,
        ILogger logger,
        CancellationToken ct)
    {
        var approvalType = ParseApprovalType(req.ApprovalType);
        if (approvalType is null)
            return Task.FromResult(Results.BadRequest(new { error = "approvalType must be one of gate, question, budget_increase" }));

        return CreateApprovalAsync(
            runId,
            principal,
            req,
            approvalType.Value,
            approvalType == ApprovalType.Question ? RunStatus.AwaitingInput : RunStatus.AwaitingApproval,
            runRepository, approvalRepository, agentRepository, manifestParser,
            eventBus, webhookDispatcher, stateMachine, logger, ct);
    }

    // ---- POST /api/agent/runs/{runId}/questions ----

    private static Task<IResult> AskQuestion(
        string runId,
        AgentQuestionRequest req,
        ClaimsPrincipal principal,
        IRunRepository runRepository,
        IApprovalRepository approvalRepository,
        IAgentRepository agentRepository,
        IAgentManifestParser manifestParser,
        IEventBus eventBus,
        IWebhookDispatcher webhookDispatcher,
        RunStateMachine stateMachine,
        ILogger logger,
        CancellationToken ct) =>
        CreateApprovalAsync(
            runId,
            principal,
            new AgentApprovalRequest
            {
                Prompt = req.Prompt,
                Options = req.Options,
                ExpiresInSeconds = req.ExpiresInSeconds,
                // A question is answered by whoever is watching the run; it is not a policy gate,
                // so it does not inherit the manifest's approvals.beforeWrite role/count.
                RequiredCount = 1,
            },
            ApprovalType.Question,
            RunStatus.AwaitingInput,
            runRepository, approvalRepository, agentRepository, manifestParser,
            eventBus, webhookDispatcher, stateMachine, logger, ct);

    private static async Task<IResult> CreateApprovalAsync(
        string runId,
        ClaimsPrincipal principal,
        AgentApprovalRequest req,
        ApprovalType approvalType,
        RunStatus targetStatus,
        IRunRepository runRepository,
        IApprovalRepository approvalRepository,
        IAgentRepository agentRepository,
        IAgentManifestParser manifestParser,
        IEventBus eventBus,
        IWebhookDispatcher webhookDispatcher,
        RunStateMachine stateMachine,
        ILogger logger,
        CancellationToken ct)
    {
        if (BindRun(runId, principal) is { } denied) return denied;

        if (string.IsNullOrWhiteSpace(req.Prompt))
            return Results.BadRequest(new { error = "prompt is required" });

        var run = await runRepository.GetAsync(runId, ct);
        if (run is null) return Results.NotFound();

        // Only a Running run can pause for a human (spec 8.2).
        if (!stateMachine.IsValidTransition(run.Status, targetStatus))
            return Results.Conflict(new
            {
                error = $"Run is {run.Status.ToDbString()}; an approval can only be raised while the run is running",
            });

        // Manifest defaults: spec.approvals.beforeWrite is the agent author's policy for gates.
        string? requiredRole = req.RequiredRole;
        var requiredCount = req.RequiredCount ?? 1;

        if (approvalType != ApprovalType.Question)
        {
            var agent = await agentRepository.GetAsync(run.AgentId, ct);
            AgentManifestApprovalGate? gate = null;
            if (agent is not null)
            {
                try
                {
                    gate = manifestParser.Parse(agent.ManifestYaml).Spec.Approvals?.BeforeWrite;
                }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Could not read approval defaults from agent {AgentId} manifest", run.AgentId);
                }
            }

            requiredRole ??= gate?.RequiredRole;
            if (req.RequiredCount is null && gate?.RequiredCount is { } count)
                requiredCount = count;
        }

        if (requiredCount < 1)
            return Results.BadRequest(new { error = "requiredCount must be at least 1" });

        if (requiredRole is not null && !IsKnownRole(requiredRole))
            return Results.BadRequest(new { error = "requiredRole must be one of owner, maintainer, developer, viewer" });

        var ttl = Math.Clamp(req.ExpiresInSeconds ?? DefaultApprovalTtlSeconds, 1, MaxApprovalTtlSeconds);
        var now = DateTime.UtcNow;

        var approval = new Approval
        {
            Id = UlidGenerator.NewUlid(),
            RunId = runId,
            StepId = req.StepId,
            ApprovalType = approvalType,
            Prompt = req.Prompt,
            Options = req.Options,
            RequiredRole = requiredRole,
            RequiredCount = requiredCount,
            Status = ApprovalStatus.Pending,
            ExpiresAt = now.AddSeconds(ttl),
            CreatedAt = now,
        };

        await approvalRepository.InsertAsync(approval, ct);
        await stateMachine.TransitionAsync(run, targetStatus, $"agent requested {approvalType.ToDbString()}", ct);

        await eventBus.PublishAsync(new RunEvent
        {
            RunId = runId,
            EventType = approvalType == ApprovalType.Question ? "question.asked" : "approval.requested",
            Level = "info",
            Message = req.Prompt,
            Payload = new
            {
                approvalId = approval.Id,
                approvalType = approvalType.ToDbString(),
                stepId = approval.StepId,
                requiredRole = approval.RequiredRole,
                requiredCount = approval.RequiredCount,
                expiresAt = approval.ExpiresAt,
            },
        }, ct);

        // Canonical webhook event name — this is the code path that was missing, which is why
        // approval.requested was in the canonical list but never dispatched.
        await webhookDispatcher.DispatchAsync(run.ProjectId, "approval.requested", new
        {
            runId,
            projectId = run.ProjectId,
            approvalId = approval.Id,
            approvalType = approvalType.ToDbString(),
            prompt = approval.Prompt,
            requiredRole = approval.RequiredRole,
            requiredCount = approval.RequiredCount,
            expiresAt = approval.ExpiresAt,
        }, ct);

        return Results.Created($"/api/approvals/{approval.Id}", new AgentApprovalCreatedResponse
        {
            ApprovalId = approval.Id,
            Status = approval.Status.ToDbString(),
            RunStatus = run.Status.ToDbString(),
            ExpiresAt = approval.ExpiresAt,
        });
    }

    // ---- GET /api/agent/runs/{runId}/approvals/{approvalId} ----

    private static async Task<IResult> GetApprovalStatus(
        string runId,
        string approvalId,
        ClaimsPrincipal principal,
        IRunRepository runRepository,
        IApprovalRepository approvalRepository,
        CancellationToken ct)
    {
        if (BindRun(runId, principal) is { } denied) return denied;

        // There is no human caller on this path — the agent is authenticated by its run token — so
        // the org to scope by comes from the run the token names, not from ICallerContext.
        var run = await runRepository.GetAsync(runId, ct);
        if (run is null) return Results.NotFound();

        var approval = await approvalRepository.GetAsync(approvalId, run.OrgId, ct);
        // Guard the cross-run case explicitly: an approval id belonging to another run must not be
        // readable just because the caller happens to hold a valid token for some run.
        if (approval is null || approval.RunId != runId) return Results.NotFound();

        var last = approval.Responses.LastOrDefault();

        return Results.Ok(new AgentApprovalStatusResponse
        {
            ApprovalId = approval.Id,
            Status = approval.Status.ToDbString(),
            RunStatus = run?.Status.ToDbString() ?? RunStatus.InfraError.ToDbString(),
            Answer = last?.Answer,
            Note = last?.Note,
            DecidedBy = approval.DecidedBy,
            DecidedAt = approval.DecidedAt,
            ExpiresAt = approval.ExpiresAt,
        });
    }

    // ---- POST /api/agent/runs/{runId}/usage ----

    private static async Task<IResult> ReportUsage(
        string runId,
        AgentUsageRequest req,
        ClaimsPrincipal principal,
        IRunRepository runRepository,
        IEventBus eventBus,
        IContainerOrchestrator orchestrator,
        RunStateMachine stateMachine,
        ILogger logger,
        CancellationToken ct)
    {
        if (BindRun(runId, principal) is { } denied) return denied;

        if (req.CostUsd < 0)
            return Results.BadRequest(new { error = "costUsd cannot be negative" });

        var run = await runRepository.GetAsync(runId, ct);
        if (run is null) return Results.NotFound();

        // Atomic increment in SQL: two concurrent reports must not lose an increment.
        var newTotal = await runRepository.AddBudgetUsageAsync(runId, req.CostUsd, ct) ?? run.BudgetUsedUsd ?? 0m;
        run.BudgetUsedUsd = newTotal;

        await eventBus.PublishAsync(new RunEvent
        {
            RunId = runId,
            EventType = "usage.reported",
            Level = "info",
            Message = $"Reported {req.CostUsd:0.####} USD of usage",
            Payload = new
            {
                costUsd = req.CostUsd,
                tokensIn = req.TokensIn,
                tokensOut = req.TokensOut,
                model = req.Model,
                budgetUsedUsd = newTotal,
                budgetMaxUsd = run.BudgetMaxUsd,
            },
        }, ct);

        var exceeded = run.BudgetMaxUsd is { } max && max > 0 && newTotal > max;
        if (exceeded && !run.Status.IsTerminal())
        {
            logger.Warning("Run {RunId} exceeded its budget ({Used} > {Max}); stopping the container",
                runId, newTotal, run.BudgetMaxUsd);

            run.ErrorCode = "budget_exceeded";
            run.ErrorMessage = $"Budget of {run.BudgetMaxUsd:0.##} USD exceeded ({newTotal:0.##} USD used)";
            run.FinishedAt = DateTime.UtcNow;

            var stop = await orchestrator.StopAsync(runId, ct);
            await stateMachine.TryTransitionAsync(run, RunStatus.BudgetExceeded, "budget exceeded", ct);

            await eventBus.PublishAsync(new RunEvent
            {
                RunId = runId,
                EventType = "budget.exceeded",
                Level = "error",
                Message = run.ErrorMessage,
                Payload = new
                {
                    budgetUsedUsd = newTotal,
                    budgetMaxUsd = run.BudgetMaxUsd,
                    // Un dépassement de budget dont l'arrêt n'est pas confirmé, c'est un agent qui
                    // continue peut-être de dépenser après que la plateforme a dit « stop ». C'est
                    // la seule information qui compte vraiment ici, elle voyage avec l'événement.
                    containerStopConfirmed = stop.Confirmed,
                    stopDetail = stop.Detail,
                },
            }, ct);

            if (!stop.Confirmed)
            {
                logger.Warning("Run {RunId} exceeded its budget but the container stop is not confirmed: {Detail}",
                    runId, stop.Detail);
            }
        }

        return Results.Ok(new AgentUsageResponse
        {
            BudgetUsedUsd = newTotal,
            BudgetMaxUsd = run.BudgetMaxUsd,
            BudgetExceeded = exceeded,
        });
    }

    // ---- helpers ----

    /// <summary>
    /// Returns a non-null result when the caller's run token is not bound to <paramref name="runId"/>.
    /// 403 rather than 404: the token is valid, it is simply not authoritative for this run, and
    /// leaking nothing more than that is the point.
    /// </summary>
    private static IResult? BindRun(string runId, ClaimsPrincipal principal)
    {
        var tokenRunId = principal.GetRunId();

        if (string.IsNullOrEmpty(tokenRunId))
            return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Run token is missing its run_id claim");

        if (!string.Equals(tokenRunId, runId, StringComparison.Ordinal))
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Run token is not valid for this run");

        return null;
    }

    private static string NormalizeLevel(string? level) => level?.ToLowerInvariant() switch
    {
        "debug" => "debug",
        "warn" or "warning" => "warn",
        "error" => "error",
        _ => "info",
    };

    private static ApprovalType? ParseApprovalType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return ApprovalType.Gate;

        try
        {
            return ApprovalTypeExtensions.FromDbString(value.ToLowerInvariant());
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool IsKnownRole(string role)
    {
        try
        {
            UserRoleExtensions.FromDbString(role.ToLowerInvariant());
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
