using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly IServiceScopeFactory? _scopeFactory;

    /// <param name="scopeFactory">
    /// Optional. When supplied, every terminal transition also appends a <c>run_history</c> entry to
    /// the project's memory. It is resolved through a fresh scope rather than by injecting
    /// <c>IMemoryService</c> directly: transitions are frequently raised from background tasks whose
    /// originating request scope is already disposed, and the indirection keeps the state machine
    /// free of a hard dependency on the memory subsystem (unit tests construct it without one).
    /// </param>
    public RunStateMachine(
        IRunRepository runRepository,
        IEventBus eventBus,
        IWebhookDispatcher webhookDispatcher,
        ILogger logger,
        IServiceScopeFactory? scopeFactory = null)
    {
        _runRepository = runRepository;
        _eventBus = eventBus;
        _webhookDispatcher = webhookDispatcher;
        _logger = logger;
        _scopeFactory = scopeFactory;
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

            await RecordRunHistoryAsync(run, newStatus, reason, ct);
        }
    }

    /// <summary>
    /// Non-throwing variant of <see cref="TransitionAsync"/> for callers that legitimately race a
    /// terminal state — e.g. the container monitor observing an exit for a run a human just
    /// cancelled. Returns false (at Debug level) instead of throwing, so an expected race does not
    /// surface as an Error-level log line and trigger false alerts.
    /// </summary>
    public async Task<bool> TryTransitionAsync(
        Run run,
        RunStatus newStatus,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (run.Status.IsTerminal() || !IsValidTransition(run.Status, newStatus))
        {
            _logger.Debug("Skipping transition of run {RunId} {From} -> {To}: not a valid transition ({Reason})",
                run.Id, run.Status, newStatus, reason ?? "n/a");
            return false;
        }

        try
        {
            await TransitionAsync(run, newStatus, reason, ct);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // Lost the race between the guard above and the write — still not an error.
            _logger.Debug(ex, "Transition of run {RunId} to {To} lost a race with a concurrent transition", run.Id, newStatus);
            return false;
        }
    }

    /// <summary>
    /// Appends the finished run to its project's memory so <c>run_history</c> — and therefore
    /// pattern detection — is populated automatically rather than only when a client happens to
    /// POST to the memory endpoint. Best-effort: a memory failure must never fail a transition.
    /// </summary>
    private async Task RecordRunHistoryAsync(Run run, RunStatus finalStatus, string? reason, CancellationToken ct)
    {
        if (_scopeFactory is null) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var agentRepository = scope.ServiceProvider.GetRequiredService<IAgentRepository>();

            var agent = await agentRepository.GetAsync(run.AgentId, ct);

            await memoryService.UpdateProjectMemoryAsync(run.ProjectId, new MemoryUpdate
            {
                NewRunHistoryItem = new RunHistoryItem
                {
                    RunId = run.Id,
                    AgentName = agent?.Name ?? run.AgentId,
                    Timestamp = run.FinishedAt ?? DateTime.UtcNow,
                    Outcome = finalStatus.ToDbString(),
                    Summary = run.ErrorMessage ?? reason ?? $"Run #{run.Number} {finalStatus.ToDbString()}",
                },
            }, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to append run {RunId} to project {ProjectId} memory", run.Id, run.ProjectId);
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
