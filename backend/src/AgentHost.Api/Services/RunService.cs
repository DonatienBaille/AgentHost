using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Issue d'une annulation. Le run passe en <c>cancelled</c> dans tous les cas — l'utilisateur l'a
/// demandé, et le laisser courir serait pire — mais <see cref="ContainerStopConfirmed"/> dit si
/// quelqu'un a effectivement confirmé l'arrêt du conteneur.
///
/// <para>C'est la distinction que l'ancienne signature (<c>Task&lt;bool&gt;</c>) ne pouvait pas
/// exprimer : « annulé » et « on a demandé l'annulation à un runner qui ne répond plus » y étaient
/// le même <c>true</c>. Avec un tier runner, le second cas devient ordinaire.</para>
/// </summary>
/// <param name="ContainerStopConfirmed">Vrai seulement si l'arrêt du conteneur est confirmé.</param>
/// <param name="Detail">Ce qui empêche la confirmation, à afficher tel quel. Null quand tout va bien.</param>
public sealed record RunCancelResult(bool ContainerStopConfirmed, string? Detail = null);

public interface IRunService
{
    Task<Run> CreateAsync(CreateRunRequest req, CancellationToken ct = default);
    Task<Run?> GetAsync(string id, CancellationToken ct = default);
    Task<List<Run>> ListAsync(int skip, int take, CancellationToken ct = default);
    Task<List<Run>> ListByProjectAsync(string projectId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<bool> ApproveAsync(string runId, ApprovalRequest req, CancellationToken ct = default);
    Task<RunCancelResult?> CancelAsync(string runId, CancellationToken ct = default);
    Task<bool> AnswerQuestionAsync(string runId, string questionId, string answer, CancellationToken ct = default);
    Task<List<RunEvent>> GetEventsAsync(string runId, long fromSeq, CancellationToken ct = default);
}

/// <summary>Run lifecycle orchestration (spec Annex A, extended with real secret retrieval and approve/cancel/answer flows).</summary>
public class RunService : IRunService
{
    private readonly IRunRepository _runRepository;
    private readonly IRunEventRepository _runEventRepository;
    private readonly IApprovalRepository _approvalRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly IAgentService _agentService;
    private readonly IAgentManifestParser _manifestParser;
    private readonly IContainerOrchestrator _orchestrator;
    private readonly IEventBus _eventBus;
    private readonly RunStateMachine _stateMachine;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAuditService _auditService;
    private readonly IWebhookDispatcher _webhookDispatcher;
    private readonly ICallerContext _callerContext;
    private readonly ILogger _logger;

    public RunService(
        IRunRepository runRepository,
        IRunEventRepository runEventRepository,
        IApprovalRepository approvalRepository,
        IProjectRepository projectRepository,
        IAgentService agentService,
        IAgentManifestParser manifestParser,
        IContainerOrchestrator orchestrator,
        IEventBus eventBus,
        RunStateMachine stateMachine,
        IServiceScopeFactory scopeFactory,
        IAuditService auditService,
        IWebhookDispatcher webhookDispatcher,
        ICallerContext callerContext,
        ILogger logger)
    {
        _runRepository = runRepository;
        _runEventRepository = runEventRepository;
        _approvalRepository = approvalRepository;
        _projectRepository = projectRepository;
        _agentService = agentService;
        _manifestParser = manifestParser;
        _orchestrator = orchestrator;
        _eventBus = eventBus;
        _stateMachine = stateMachine;
        _scopeFactory = scopeFactory;
        _auditService = auditService;
        _webhookDispatcher = webhookDispatcher;
        _callerContext = callerContext;
        _logger = logger;
    }

    /// <summary>
    /// The acting user, taken from the JWT — never from the request body. Null when there is no
    /// HTTP caller on the ambient scope (SignalR hub invocations, background work): those paths
    /// authorize the run against Context.User themselves before calling in.
    /// </summary>
    private string? CallerUserId => _callerContext.IsAuthenticated ? _callerContext.UserId : null;

    /// <summary>The caller's organization, for the list methods below. Requires an HTTP caller.</summary>
    private string CallerOrgId => _callerContext.OrgId;

    public async Task<Run> CreateAsync(CreateRunRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(req.AgentId))
            throw new ArgumentException("AgentId required");

        var agent = await _agentService.GetAsync(req.AgentId, ct)
            ?? throw new KeyNotFoundException($"Agent {req.AgentId} not found");

        if (string.IsNullOrEmpty(agent.CurrentVersionId))
            throw new InvalidOperationException($"Agent {req.AgentId} has no published version");

        // The agent already belongs to exactly one project — derive it rather than trusting
        // (or requiring) the caller to pass a redundant projectId that could disagree with it.
        var projectId = agent.ProjectId;
        var project = await _projectRepository.GetAsync(projectId, ct)
            ?? throw new KeyNotFoundException($"Project {projectId} not found");

        var manifest = _manifestParser.Parse(agent.ManifestYaml);

        var now = DateTime.UtcNow;
        var budgetMaxUsd = ResolveRunBudget(req.BudgetMaxUsd, manifest);
        await EnsureProjectMonthlyBudgetAvailableAsync(project, now, ct);

        var number = await _runRepository.GetNextRunNumberAsync(projectId, ct);
        var run = new Run
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = project.OrgId,
            ProjectId = projectId,
            Number = number,
            AgentId = req.AgentId,
            AgentVersionId = agent.CurrentVersionId,
            Status = RunStatus.Pending,
            Inputs = req.Inputs ?? new JsonObject(),
            Context = req.Context ?? new JsonObject(),
            BudgetMaxUsd = budgetMaxUsd,
            BudgetUsedUsd = 0m,
            TriggeredByUserId = CallerUserId,
            TriggeredByType = req.TriggeredByType,
            ParentRunId = req.ParentRunId,
            RootRunId = req.ParentRunId, // simplification: root = immediate parent unless chained further
            CreatedAt = now,
            UpdatedAt = now,
            RuntimeProfile = new RuntimeProfile
            {
                Profile = manifest.Spec.Runtime.Profile,
                Cpu = manifest.Spec.Runtime.Cpu,
                Memory = manifest.Spec.Runtime.Memory,
                Disk = manifest.Spec.Runtime.Disk,
                MaxDurationSeconds = manifest.Spec.Runtime.MaxDurationSeconds,
            },
        };

        await _runRepository.InsertAsync(run, ct);

        await _eventBus.PublishAsync(new RunEvent
        {
            RunId = run.Id,
            EventType = "run.created",
            Level = "info",
            Message = $"Run created for agent {agent.Name}",
        }, ct);

        await _auditService.RecordAsync(
            project.OrgId, "run.created", run.TriggeredByUserId, "run", run.Id, ct: ct);

        await _webhookDispatcher.DispatchAsync(run.ProjectId, "run.created", new
        {
            runId = run.Id,
            projectId = run.ProjectId,
            agentId = run.AgentId,
            status = run.Status.ToDbString(),
        }, ct);

        await _stateMachine.TransitionAsync(run, RunStatus.Queued, ct: ct);

        // Launch in the background via a fresh DI scope: the run can outlive the HTTP request
        // that created it by hours, so it must not depend on the request's scoped services.
        _ = ExecuteRunInNewScopeAsync(run.Id);

        return run;
    }

    /// <summary>
    /// Resolves the run's budget cap. A caller-supplied <c>budgetMaxUsd</c> was previously accepted
    /// unbounded, letting a client grant itself an arbitrary spend; the manifest's
    /// <c>budget.hardMaxUsd</c> is the agent author's ceiling and is now enforced.
    /// </summary>
    private static decimal ResolveRunBudget(decimal? requested, AgentManifest manifest)
    {
        var hardMax = manifest.Spec.Budget.HardMaxUsd;

        if (requested is null)
            return Math.Min(manifest.Spec.Budget.DefaultMaxUsd, hardMax > 0 ? hardMax : manifest.Spec.Budget.DefaultMaxUsd);

        if (requested < 0)
            throw new ArgumentException("budgetMaxUsd cannot be negative");

        if (hardMax > 0 && requested > hardMax)
            throw new InvalidOperationException(
                $"budgetMaxUsd {requested:0.##} exceeds the agent manifest's budget.hardMaxUsd of {hardMax:0.##}");

        return requested.Value;
    }

    /// <summary>
    /// Refuses to start a run once the project has consumed its <c>budget_monthly_usd</c> for the
    /// current calendar month (sum of every run's <c>budget_used_usd</c> created this month).
    /// </summary>
    private async Task EnsureProjectMonthlyBudgetAvailableAsync(Project project, DateTime nowUtc, CancellationToken ct)
    {
        if (project.BudgetMonthlyUsd <= 0) return;

        var monthStart = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var used = await _runRepository.SumBudgetUsedForProjectSinceAsync(project.Id, monthStart, ct);

        if (used >= project.BudgetMonthlyUsd)
            throw new InvalidOperationException(
                $"Project monthly budget exhausted: {used:0.##} of {project.BudgetMonthlyUsd:0.##} USD already spent this month");
    }

    private async Task ExecuteRunInNewScopeAsync(string runId)
    {
        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var agentServiceScoped = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var manifestParser = scope.ServiceProvider.GetRequiredService<IAgentManifestParser>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IContainerOrchestrator>();
        var secretsBroker = scope.ServiceProvider.GetRequiredService<ISecretsBroker>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RunStateMachine>();
        var runTokenService = scope.ServiceProvider.GetRequiredService<IRunTokenService>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger>();

        var run = await runRepository.GetAsync(runId, CancellationToken.None);
        if (run is null)
        {
            logger.Error("Run {RunId} disappeared before execution could start", runId);
            return;
        }

        var agent = await agentServiceScoped.GetAsync(run.AgentId, CancellationToken.None);
        if (agent is null)
        {
            logger.Error("Agent {AgentId} not found while executing run {RunId}", run.AgentId, runId);
            run.ErrorCode = "agent_not_found";
            // Routed through the state machine (rather than a direct repository update) so the
            // terminal-state webhook dispatch (run.infra_error / run.finished) still fires.
            await stateMachine.TransitionAsync(run, RunStatus.InfraError, "agent not found", CancellationToken.None);
            return;
        }

        try
        {
            var manifest = manifestParser.Parse(agent.ManifestYaml);

            run.RuntimeProfile = new RuntimeProfile
            {
                Profile = manifest.Spec.Runtime.Profile,
                Cpu = manifest.Spec.Runtime.Cpu,
                Memory = manifest.Spec.Runtime.Memory,
                Disk = manifest.Spec.Runtime.Disk,
                MaxDurationSeconds = manifest.Spec.Runtime.MaxDurationSeconds,
            };

            // The orchestrator creates {WorkspacePath}/{runId}/{workspace,secrets} but never told
            // anyone about it; record the run's directory so `runs.workspace_path` is actually
            // populated. Set before the first transition so that transition's UPDATE persists it.
            var workspaceRoot = configuration["Docker:WorkspacePath"] ?? "/var/agenthost/runs";
            run.WorkspacePath = Path.Combine(workspaceRoot, run.Id);

            // Run-scoped callback credential for the agent protocol (docs/agent-protocol.md).
            // Minted per launch, never persisted, and injected as AGENTHOST_RUN_TOKEN.
            run.AgentRunToken = runTokenService.Issue(run);

            await stateMachine.TransitionAsync(run, RunStatus.Provisioning, ct: CancellationToken.None);
            await stateMachine.TransitionAsync(run, RunStatus.Preparing, ct: CancellationToken.None);

            var secrets = await secretsBroker.ResolveForRunAsync(
                run.OrgId, run.ProjectId, run.Id, manifest.Spec.Permissions.Secrets, CancellationToken.None);

            var containerId = await orchestrator.LaunchAgentAsync(run, agent, secrets, CancellationToken.None);

            logger.Information("Run {RunId} launched in container {ContainerId}", run.Id, containerId);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to execute run {RunId}", run.Id);

            run.ErrorCode = "execution_error";
            run.ErrorMessage = ex.Message;
            // RunStatus.Failed is only a valid transition from Finalizing (spec 8.2); this catch
            // can fire earlier (e.g. during Provisioning/Preparing), so InfraError — valid from
            // any non-terminal state — is the correct terminal status here. Routing through the
            // state machine (rather than a direct repository update) also ensures the
            // terminal-state webhook dispatch (run.infra_error / run.finished) fires.
            await stateMachine.TransitionAsync(run, RunStatus.InfraError, ex.Message, CancellationToken.None);
        }
    }

    public Task<Run?> GetAsync(string id, CancellationToken ct = default) => _runRepository.GetAsync(id, ct);

    public Task<List<Run>> ListAsync(int skip, int take, CancellationToken ct = default) =>
        _runRepository.ListByOrgAsync(CallerOrgId, skip, take, ct);

    public Task<List<Run>> ListByProjectAsync(string projectId, int skip = 0, int take = 50, CancellationToken ct = default) =>
        _runRepository.ListByProjectAsync(projectId, CallerOrgId, skip, take, ct);

    public async Task<bool> ApproveAsync(string runId, ApprovalRequest req, CancellationToken ct = default)
    {
        var run = await _runRepository.GetAsync(runId, ct);
        if (run is null || run.Status != RunStatus.AwaitingApproval)
            return false;

        var approval = await _approvalRepository.GetPendingForRunAsync(runId, ct);

        if (!CallerMaySatisfy(approval))
            return false;

        var decision = (req.Decision ?? "approve").ToLowerInvariant();
        var decidedBy = CallerUserId;

        if (approval is not null)
        {
            approval.Responses.Add(new ApprovalResponse
            {
                By = decidedBy ?? "unknown",
                Decision = decision,
                At = DateTime.UtcNow,
                Note = req.Note,
            });

            if (decision == "reject")
            {
                approval.Status = ApprovalStatus.Rejected;
                approval.DecidedAt = DateTime.UtcNow;
                approval.DecidedBy = decidedBy;
            }
            else if (approval.Responses.Count >= approval.RequiredCount)
            {
                approval.Status = ApprovalStatus.Approved;
                approval.DecidedAt = DateTime.UtcNow;
                approval.DecidedBy = decidedBy;
            }

            await _approvalRepository.UpdateAsync(approval, ct);
        }

        if (decision == "reject")
        {
            await _stateMachine.TransitionAsync(run, RunStatus.Rejected, "approval rejected", ct);
            await _eventBus.PublishAsync(new RunEvent
            {
                RunId = runId,
                EventType = "approval.rejected",
                Level = "warn",
                Message = "Approval rejected",
                Payload = new { req.StepId, req.Note },
            }, ct);
            return true;
        }

        if (approval is null || approval.Status == ApprovalStatus.Approved)
        {
            await _stateMachine.TransitionAsync(run, RunStatus.Running, "approved", ct);
            await _eventBus.PublishAsync(new RunEvent
            {
                RunId = runId,
                EventType = "approval.granted",
                Level = "info",
                Message = "Approval granted, run resumed",
                Payload = new { req.StepId, req.Note },
            }, ct);
        }

        return true;
    }

    /// <summary>
    /// Enforces the approval's <c>required_role</c>: a decision from a caller below that role (or
    /// from a caller we cannot identify at all) is refused. Without this, an approval gate the
    /// agent asked a maintainer for could be satisfied by any developer.
    /// </summary>
    private bool CallerMaySatisfy(Approval? approval)
    {
        if (approval is null || string.IsNullOrWhiteSpace(approval.RequiredRole))
            return true;

        UserRole required;
        try
        {
            required = UserRoleExtensions.FromDbString(approval.RequiredRole);
        }
        catch (ArgumentOutOfRangeException)
        {
            _logger.Warning("Approval {ApprovalId} has unrecognized required_role {Role}; refusing the decision",
                approval.Id, approval.RequiredRole);
            return false;
        }

        if (!_callerContext.IsAuthenticated)
        {
            _logger.Warning("Approval {ApprovalId} requires role {Role} but the caller could not be identified",
                approval.Id, approval.RequiredRole);
            return false;
        }

        if (!_callerContext.HasAtLeast(required))
        {
            _logger.Warning("User {UserId} ({Role}) may not decide approval {ApprovalId} which requires {Required}",
                _callerContext.UserId, _callerContext.Role, approval.Id, required);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Annule le run. Renvoie <c>null</c> quand il n'y a rien à annuler (run inexistant ou déjà
    /// terminal) — c'est le <c>false</c> d'avant.
    ///
    /// <para>Quand l'arrêt du conteneur n'est pas confirmé, le run passe quand même en
    /// <c>cancelled</c>, mais un événement <c>run.cancel_unconfirmed</c> de niveau <c>warn</c> est
    /// publié sur sa chronologie. C'est ce qui rend la dégradation visible à l'écran : le corps
    /// d'une réponse HTTP se perd, la chronologie du run reste.</para>
    /// </summary>
    public async Task<RunCancelResult?> CancelAsync(string runId, CancellationToken ct = default)
    {
        var run = await _runRepository.GetAsync(runId, ct);
        if (run is null || run.Status.IsTerminal())
            return null;

        var stop = await _orchestrator.StopAsync(runId, ct);
        await _stateMachine.TransitionAsync(run, RunStatus.Cancelled, "cancelled by user", ct);

        await _auditService.RecordAsync(run.OrgId, "run.cancelled", CallerUserId ?? run.TriggeredByUserId, "run", run.Id, ct: ct);

        if (stop.Confirmed)
            return new RunCancelResult(true);

        _logger.Warning("Run {RunId} was cancelled but the container stop is not confirmed: {Detail}",
            runId, stop.Detail);

        await _eventBus.PublishAsync(new RunEvent
        {
            RunId = runId,
            EventType = "run.cancel_unconfirmed",
            Level = "warn",
            Message = "Run marked cancelled, but no node confirmed that its container was stopped. " +
                      "It may still be executing.",
            Payload = new { outcome = stop.Outcome.ToString(), detail = stop.Detail },
        }, ct);

        return new RunCancelResult(false, stop.Detail);
    }

    public async Task<bool> AnswerQuestionAsync(string runId, string questionId, string answer, CancellationToken ct = default)
    {
        var run = await _runRepository.GetAsync(runId, ct);
        if (run is null || run.Status != RunStatus.AwaitingInput)
            return false;

        var approval = await _approvalRepository.GetPendingForRunAsync(runId, ct);

        if (!CallerMaySatisfy(approval))
            return false;

        if (approval is not null)
        {
            approval.Responses.Add(new ApprovalResponse
            {
                By = CallerUserId ?? "user",
                Decision = "answer",
                Answer = answer,
                At = DateTime.UtcNow,
            });
            approval.Status = ApprovalStatus.Approved;
            approval.DecidedAt = DateTime.UtcNow;
            approval.DecidedBy = CallerUserId;
            await _approvalRepository.UpdateAsync(approval, ct);
        }

        await _stateMachine.TransitionAsync(run, RunStatus.Running, "question answered", ct);

        await _eventBus.PublishAsync(new RunEvent
        {
            RunId = runId,
            EventType = "question.answered",
            Level = "info",
            Message = "Question answered, run resumed",
            Payload = new { questionId, answer },
        }, ct);

        return true;
    }

    public Task<List<RunEvent>> GetEventsAsync(string runId, long fromSeq, CancellationToken ct = default) =>
        _runEventRepository.ListByRunAsync(runId, fromSeq, ct);
}
