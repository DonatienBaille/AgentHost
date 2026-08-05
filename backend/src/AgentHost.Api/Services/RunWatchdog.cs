using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Background sweeper that enforces the two deadlines the system records but previously never
/// acted on:
///
/// 1. <c>maxDurationSeconds</c> — Docker's StopTimeout only bounds an explicit <c>docker stop</c>,
///    so a runaway agent would otherwise run forever and <see cref="RunStatus.TimedOut"/> was
///    unreachable. Runs past <c>started_at + maxDurationSeconds</c> get their container stopped and
///    the run transitioned to <c>timed_out</c>.
/// 2. <c>approvals.expires_at</c> — a pending gate/question nobody answers left the run parked in
///    <c>awaiting_approval</c>/<c>awaiting_input</c> indefinitely. Expired approvals are marked
///    <c>expired</c> and their run transitioned to <c>timed_out</c> (see the RunStateMachine class
///    remarks on why "expired" maps onto TimedOut).
///
/// The scan interval is configurable via <c>Watchdog:IntervalSeconds</c> (default 30). Every run and
/// every approval is handled in its own try/catch: one bad row must never kill the loop.
/// </summary>
public class RunWatchdog : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    public RunWatchdog(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var seconds = int.TryParse(config["Watchdog:IntervalSeconds"], out var s) && s > 0 ? s : 30;
        _interval = TimeSpan.FromSeconds(seconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Run watchdog started (interval {Interval}s)", _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Delay first: nothing can be overdue in the instant the host starts, and this keeps
            // the sweep off the startup path (migrations, warm-up).
            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ScanOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Never let a scan failure end the loop.
                _logger.Error(ex, "Run watchdog scan failed; will retry at the next interval");
            }
        }

        _logger.Information("Run watchdog stopped");
    }

    /// <summary>
    /// One full sweep. Public so it can be invoked deterministically (tests, or an operator-driven
    /// trigger) rather than only on the timer.
    /// </summary>
    public async Task ScanOnceAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        await ExpireOverdueRunsAsync(sp, ct);
        await ExpireOverdueApprovalsAsync(sp, ct);
    }

    private async Task ExpireOverdueRunsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var runRepository = sp.GetRequiredService<IRunRepository>();
        var agentRepository = sp.GetRequiredService<IAgentRepository>();
        var manifestParser = sp.GetRequiredService<IAgentManifestParser>();
        var orchestrator = sp.GetRequiredService<IContainerOrchestrator>();
        var stateMachine = sp.GetRequiredService<RunStateMachine>();
        var eventBus = sp.GetRequiredService<IEventBus>();

        var runs = await runRepository.ListActiveStartedAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var run in runs)
        {
            try
            {
                // maxDurationSeconds lives in the agent manifest, not on the runs table, so it has
                // to be re-derived per run. The active-run set is small by construction.
                var agent = await agentRepository.GetAsync(run.AgentId, ct);
                if (agent is null) continue;

                var maxDuration = manifestParser.Parse(agent.ManifestYaml).Spec.Runtime.MaxDurationSeconds;
                if (maxDuration <= 0 || run.StartedAt is null) continue;

                var deadline = run.StartedAt.Value.AddSeconds(maxDuration);
                if (deadline > now) continue;

                _logger.Warning("Run {RunId} exceeded its {MaxDuration}s limit (started {StartedAt:o}); timing it out",
                    run.Id, maxDuration, run.StartedAt.Value);

                run.ErrorCode = "timeout";
                run.ErrorMessage = $"Run exceeded its maximum duration of {maxDuration}s";
                run.FinishedAt = now;
                run.DurationMs = (long)(now - run.StartedAt.Value).TotalMilliseconds;

                await orchestrator.StopAsync(run.Id, ct);

                if (await stateMachine.TryTransitionAsync(run, RunStatus.TimedOut, "max duration exceeded", ct))
                {
                    await eventBus.PublishAsync(new RunEvent
                    {
                        RunId = run.Id,
                        EventType = "run.timed_out",
                        Level = "error",
                        Message = run.ErrorMessage,
                        Payload = new { maxDurationSeconds = maxDuration, startedAt = run.StartedAt },
                    }, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Watchdog failed to process run {RunId}", run.Id);
            }
        }
    }

    private async Task ExpireOverdueApprovalsAsync(IServiceProvider sp, CancellationToken ct)
    {
        var approvalRepository = sp.GetRequiredService<IApprovalRepository>();
        var runRepository = sp.GetRequiredService<IRunRepository>();
        var stateMachine = sp.GetRequiredService<RunStateMachine>();
        var eventBus = sp.GetRequiredService<IEventBus>();

        var now = DateTime.UtcNow;
        var expired = await approvalRepository.ListExpiredPendingAsync(now, ct);

        foreach (var approval in expired)
        {
            try
            {
                approval.Status = ApprovalStatus.Expired;
                approval.DecidedAt = now;
                await approvalRepository.UpdateAsync(approval, ct);

                await eventBus.PublishAsync(new RunEvent
                {
                    RunId = approval.RunId,
                    EventType = "approval.expired",
                    Level = "warn",
                    Message = $"Approval expired without a decision: {approval.Prompt}",
                    Payload = new { approvalId = approval.Id, expiresAt = approval.ExpiresAt },
                }, ct);

                var run = await runRepository.GetAsync(approval.RunId, ct);
                if (run is null || run.Status.IsTerminal()) continue;

                run.ErrorCode = "approval_expired";
                run.ErrorMessage = "Approval window expired without a decision";
                run.FinishedAt = now;

                await stateMachine.TryTransitionAsync(run, RunStatus.TimedOut, "approval expired", ct);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Watchdog failed to expire approval {ApprovalId}", approval.Id);
            }
        }
    }
}
