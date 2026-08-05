using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace AgentHost.Api.Hubs;

/// <summary>
/// Real-time hub for a single run's events, approvals and Q&amp;A (spec section 10.1).
///
/// The hub endpoint requires authentication, but that only proves the caller is *some* user — it
/// says nothing about the run they name. Every method below re-checks that the run belongs to the
/// caller's organization (read from the JWT via <c>Context.User</c>) before joining a group or
/// acting on it: joining <c>run-{runId}</c> subscribes the connection to a live feed of that run's
/// logs, tool calls and approval prompts, so an unchecked join was a cross-tenant firehose.
/// </summary>
public class RunHub : Hub
{
    private readonly IRunService _runService;
    private readonly IRunRepository _runRepository;
    private readonly ILogger _logger;

    public RunHub(IRunService runService, IRunRepository runRepository, ILogger logger)
    {
        _runService = runService;
        _runRepository = runRepository;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("connected", new { connectionId = Context.ConnectionId });
        await base.OnConnectedAsync();
    }

    /// <summary>Client joins a run (subscribes to updates), if it belongs to their organization.</summary>
    public async Task JoinRun(string runId)
    {
        var run = await ResolveAuthorizedRunAsync(runId);
        if (run is null) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"run-{runId}");
        await Clients.Caller.SendAsync("runState", run);
    }

    /// <summary>Client leaves a run.</summary>
    public async Task LeaveRun(string runId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"run-{runId}");
    }

    /// <summary>Client approves a step.</summary>
    public async Task ApproveStep(string runId, string stepId, string? note = null)
    {
        if (await ResolveAuthorizedRunAsync(runId) is null) return;

        try
        {
            var success = await _runService.ApproveAsync(runId, new ApprovalRequest
            {
                StepId = stepId,
                Decision = "approve",
                Note = note,
            }, Context.ConnectionAborted);

            if (success)
            {
                await Clients.Group($"run-{runId}")
                    .SendAsync("stepApproved", new { stepId, timestamp = DateTime.UtcNow });
            }
            else
            {
                await Clients.Caller.SendAsync("error", "Approval failed");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error approving step {StepId} for run {RunId}", stepId, runId);
            await Clients.Caller.SendAsync("error", ex.Message);
        }
    }

    /// <summary>Answers a question raised by the agent.</summary>
    public async Task AnswerQuestion(string runId, string questionId, string answer)
    {
        if (await ResolveAuthorizedRunAsync(runId) is null) return;

        try
        {
            var success = await _runService.AnswerQuestionAsync(runId, questionId, answer, Context.ConnectionAborted);

            if (success)
            {
                await Clients.Group($"run-{runId}")
                    .SendAsync("questionAnswered", new { questionId, answer });
            }
            else
            {
                await Clients.Caller.SendAsync("error", "Failed to answer question");
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error answering question {QuestionId} for run {RunId}", questionId, runId);
            await Clients.Caller.SendAsync("error", ex.Message);
        }
    }

    /// <summary>
    /// Loads the run only if it belongs to the caller's org; otherwise sends an <c>error</c> to
    /// the caller and returns null. The message is identical for "no such run" and "not yours", so
    /// the hub cannot be used to probe run ids for existence.
    /// </summary>
    private async Task<Run?> ResolveAuthorizedRunAsync(string runId)
    {
        var orgId = Context.User.GetOrgId();
        if (string.IsNullOrEmpty(orgId))
        {
            await Clients.Caller.SendAsync("error", "Not authenticated");
            return null;
        }

        var run = await _runRepository.GetAsync(runId, orgId, Context.ConnectionAborted);
        if (run is null)
        {
            _logger.Warning("Connection {ConnectionId} (org {OrgId}) was denied access to run {RunId}",
                Context.ConnectionId, orgId, runId);
            await Clients.Caller.SendAsync("error", $"Run {runId} not found");
            return null;
        }

        return run;
    }
}
