using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Enforces the run lifecycle transition table from spec section 8.2.
///
/// Deviation from the spec snippet: the spec's C# switch references a `RunStatus.Expired`
/// target for `AwaitingApproval`/`AwaitingInput` timing out, but section 8.1's authoritative
/// status enum (and this project's task brief) has no `Expired` state — the terminal states are
/// exactly succeeded/failed/cancelled/timed_out/budget_exceeded/rejected/infra_error. We map
/// "approval/question window expired" onto `TimedOut`, which is the closest existing terminal
/// state and keeps the enum consistent with the schema's `status VARCHAR(50)` comment.
/// </summary>
public class RunStateMachine
{
    private readonly IRunRepository _runRepository;
    private readonly IEventBus _eventBus;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ILogger _logger;

    public RunStateMachine(IRunRepository runRepository, IEventBus eventBus, IWebhookDispatcher webhookDispatcher, ILogger logger)
    {
        _runRepository = runRepository;
        _eventBus = eventBus;
        _webhookDispatcher = webhookDispatcher;
        _logger = logger;
    }

    public async Task TransitionAsync(
        Run run,
        RunStatus newStatus,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (run.Status.IsTerminal())
            throw new InvalidOperationException($"Cannot transition from terminal state {run.Status}");

        if (!IsValidTransition(run.Status, newStatus))
            throw new InvalidOperationException(
                $"Invalid transition from {run.Status} to {newStatus}");

        var previousStatus = run.Status;
        run.Status = newStatus;
        run.UpdatedAt = DateTime.UtcNow;

        await _runRepository.UpdateAsync(run, ct);

        await _eventBus.PublishAsync(new RunEvent
        {
            RunId = run.Id,
            EventType = "run.status_changed",
            Level = "info",
            Message = $"Status changed: {previousStatus} → {newStatus}",
            Payload = new { from = previousStatus.ToDbString(), to = newStatus.ToDbString(), reason },
        }, ct);

        _logger.Information("Run {RunId} transitioned {From} -> {To} ({Reason})",
            run.Id, previousStatus, newStatus, reason ?? "n/a");

        if (newStatus.IsTerminal())
        {
            var payload = new { runId = run.Id, projectId = run.ProjectId, status = newStatus.ToDbString() };
            await _webhookDispatcher.DispatchAsync(run.ProjectId, $"run.{newStatus.ToDbString()}", payload, ct);
            await _webhookDispatcher.DispatchAsync(run.ProjectId, "run.finished", payload, ct);
        }
    }

    public bool IsValidTransition(RunStatus from, RunStatus to)
    {
        return (from, to) switch
        {
            (RunStatus.Pending, RunStatus.Queued) => true,
            (RunStatus.Queued, RunStatus.Provisioning) => true,
            (RunStatus.Provisioning, RunStatus.Preparing) => true,
            (RunStatus.Preparing, RunStatus.Running) => true,
            (RunStatus.Running, RunStatus.AwaitingApproval) => true,
            (RunStatus.Running, RunStatus.AwaitingInput) => true,
            (RunStatus.Running, RunStatus.Finalizing) => true,
            (RunStatus.AwaitingApproval, RunStatus.Running) => true,
            (RunStatus.AwaitingApproval, RunStatus.TimedOut) => true, // "expired" (see class remarks)
            (RunStatus.AwaitingInput, RunStatus.Running) => true,
            (RunStatus.AwaitingInput, RunStatus.TimedOut) => true, // "expired" (see class remarks)
            (RunStatus.Finalizing, RunStatus.Succeeded) => true,
            (RunStatus.Finalizing, RunStatus.Failed) => true,
            (_, RunStatus.Cancelled) => !from.IsTerminal(),
            (_, RunStatus.TimedOut) => !from.IsTerminal(),
            (_, RunStatus.BudgetExceeded) => !from.IsTerminal(),
            (_, RunStatus.Rejected) => !from.IsTerminal(),
            (_, RunStatus.InfraError) => !from.IsTerminal(),
            _ => false,
        };
    }
}
