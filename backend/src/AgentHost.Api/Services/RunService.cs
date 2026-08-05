using System.Text.Json.Nodes;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AgentHost.Api.Services;

public interface IRunService
{
    Task<Run> CreateAsync(CreateRunRequest req, CancellationToken ct = default);
    Task<Run?> GetAsync(string id, CancellationToken ct = default);
    Task<List<Run>> ListAsync(int skip, int take, CancellationToken ct = default);
    Task<List<Run>> ListByProjectAsync(string projectId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<bool> ApproveAsync(string runId, ApprovalRequest req, CancellationToken ct = default);
    Task<bool> CancelAsync(string runId, CancellationToken ct = default);
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
        _logger = logger;
    }

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
        var number = await _runRepository.GetNextRunNumberAsync(projectId, ct);

        var now = DateTime.UtcNow;
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
            BudgetMaxUsd = req.BudgetMaxUsd ?? manifest.Spec.Budget.DefaultMaxUsd,
            BudgetUsedUsd = 0m,
            TriggeredByUserId = req.TriggeredByUserId,
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
            project.OrgId, "run.created", req.TriggeredByUserId, "run", run.Id, ct: ct);

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

    private async Task ExecuteRunInNewScopeAsync(string runId)
    {
        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var agentServiceScoped = scope.ServiceProvider.GetRequiredService<IAgentService>();
        var manifestParser = scope.ServiceProvider.GetRequiredService<IAgentManifestParser>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IContainerOrchestrator>();
        var secretsBroker = scope.ServiceProvider.GetRequiredService<ISecretsBroker>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RunStateMachine>();
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
            await stateMachine.TransitionAsync(run, RunStatus.Provisioning, ct: CancellationToken.None);
            await stateMachine.TransitionAsync(run, RunStatus.Preparing, ct: CancellationToken.None);

            var manifest = manifestParser.Parse(agent.ManifestYaml);
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
        _runRepository.ListAsync(skip, take, ct);

    public Task<List<Run>> ListByProjectAsync(string projectId, int skip = 0, int take = 50, CancellationToken ct = default) =>
        _runRepository.ListByProjectAsync(projectId, skip, take, ct);

    public async Task<bool> ApproveAsync(string runId, ApprovalRequest req, CancellationToken ct = default)
    {
        var run = await _runRepository.GetAsync(runId, ct);
        if (run is null || run.Status != RunStatus.AwaitingApproval)
            return false;

        var approval = await _approvalRepository.GetPendingForRunAsync(runId, ct);

        var decision = (req.Decision ?? "approve").ToLowerInvariant();

        if (approval is not null)
        {
            approval.Responses.Add(new ApprovalResponse
            {
                By = req.DecidedByUserId ?? "unknown",
                Decision = decision,
                At = DateTime.UtcNow,
                Note = req.Note,
            });

            if (decision == "reject")
            {
                approval.Status = ApprovalStatus.Rejected;
                approval.DecidedAt = DateTime.UtcNow;
                approval.DecidedBy = req.DecidedByUserId;
            }
            else if (approval.Responses.Count >= approval.RequiredCount)
            {
                approval.Status = ApprovalStatus.Approved;
                approval.DecidedAt = DateTime.UtcNow;
                approval.DecidedBy = req.DecidedByUserId;
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

    public async Task<bool> CancelAsync(string runId, CancellationToken ct = default)
    {
        var run = await _runRepository.GetAsync(runId, ct);
        if (run is null || run.Status.IsTerminal())
            return false;

        await _orchestrator.StopAsync(runId, ct);
        await _stateMachine.TransitionAsync(run, RunStatus.Cancelled, "cancelled by user", ct);

        await _auditService.RecordAsync(run.OrgId, "run.cancelled", run.TriggeredByUserId, "run", run.Id, ct: ct);

        return true;
    }

    public async Task<bool> AnswerQuestionAsync(string runId, string questionId, string answer, CancellationToken ct = default)
    {
        var run = await _runRepository.GetAsync(runId, ct);
        if (run is null || run.Status != RunStatus.AwaitingInput)
            return false;

        var approval = await _approvalRepository.GetPendingForRunAsync(runId, ct);
        if (approval is not null)
        {
            approval.Responses.Add(new ApprovalResponse
            {
                By = "user",
                Decision = "answer",
                Answer = answer,
                At = DateTime.UtcNow,
            });
            approval.Status = ApprovalStatus.Approved;
            approval.DecidedAt = DateTime.UtcNow;
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
