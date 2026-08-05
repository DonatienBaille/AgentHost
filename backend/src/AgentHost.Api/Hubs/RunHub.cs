using AgentHost.Api.Contracts;
using AgentHost.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace AgentHost.Api.Hubs;

/// <summary>Real-time hub for a single run's events, approvals and Q&amp;A (spec section 10.1).</summary>
public class RunHub : Hub
{
    private readonly IRunService _runService;
    private readonly ILogger _logger;

    public RunHub(IRunService runService, ILogger logger)
    {
        _runService = runService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("connected", new { connectionId = Context.ConnectionId });
        await base.OnConnectedAsync();
    }

    /// <summary>Client joins a run (subscribes to updates).</summary>
    public async Task JoinRun(string runId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"run-{runId}");

        var run = await _runService.GetAsync(runId, Context.ConnectionAborted);
        if (run != null)
        {
            await Clients.Caller.SendAsync("runState", run);
        }
    }

    /// <summary>Client leaves a run.</summary>
    public async Task LeaveRun(string runId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"run-{runId}");
    }

    /// <summary>Client approves a step.</summary>
    public async Task ApproveStep(string runId, string stepId, string? note = null)
    {
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
}
