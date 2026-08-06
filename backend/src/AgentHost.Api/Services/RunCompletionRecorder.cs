using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Ce qu'il advient d'un run une fois son conteneur sorti : code de sortie, durée, transitions
/// terminales, événement <c>run.finished</c>.
///
/// <para>Extrait de <c>ContainerOrchestrator.MonitorContainerAsync</c> parce que les deux modes
/// (<c>inprocess</c> et <c>remote</c>) surveillent des conteneurs à des endroits différents mais
/// doivent en tirer exactement les mêmes conséquences. Dupliquer ces transitions aurait garanti
/// qu'elles divergent — et une divergence ici se lit comme « les runs distants ne finissent jamais
/// en succeeded ».</para>
///
/// <para>Chaque appel ouvre sa propre portée DI : l'appelant est une tâche d'arrière-plan qui peut
/// survivre des heures à la requête HTTP d'origine, dont les services à portée sont libérés depuis
/// longtemps.</para>
/// </summary>
public sealed class RunCompletionRecorder
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    public RunCompletionRecorder(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Enregistre la sortie d'un conteneur. <paramref name="logLength"/> n'est là que pour la charge
    /// utile de l'événement : les journaux eux-mêmes ne sont pas persistés (ils restent au nœud).
    /// </summary>
    public async Task RecordExitAsync(
        string runId, long exitCode, DateTime startedAt, int logLength, string? containerId = null)
    {
        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var eventBus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RunStateMachine>();

        var run = await runRepository.GetAsync(runId, CancellationToken.None);
        if (run is null)
        {
            _logger.Warning("Run {RunId} not found while finalizing container {ContainerId}", runId, containerId);
            return;
        }

        run.ExitCode = (int)exitCode;
        run.FinishedAt = DateTime.UtcNow;
        run.DurationMs = (long)(run.FinishedAt.Value - startedAt).TotalMilliseconds;
        if (exitCode != 0)
        {
            run.ErrorCode = "non_zero_exit";
            run.ErrorMessage = $"Container exited with code {exitCode}";
        }

        // Succeeded/Failed ne sont atteignables que depuis Finalizing (spec 8.2).
        await stateMachine.TransitionAsync(run, RunStatus.Finalizing, "container exited", CancellationToken.None);
        await stateMachine.TransitionAsync(
            run, exitCode == 0 ? RunStatus.Succeeded : RunStatus.Failed, "container exited", CancellationToken.None);

        await eventBus.PublishAsync(new RunEvent
        {
            RunId = runId,
            EventType = "run.finished",
            Level = "info",
            Message = $"Run finished with exit code {exitCode}",
            Payload = new { exitCode, duration = run.DurationMs, logLength },
        }, CancellationToken.None);
    }

    /// <summary>
    /// La surveillance elle-même a échoué (démon injoignable, runner disparu en cours d'attente).
    /// Le run part en <c>infra_error</c> plutôt que de rester bloqué en <c>running</c> jusqu'à ce
    /// que le watchdog s'en occupe — sauf s'il est déjà terminal, auquel cas il n'y a rien à dire.
    /// </summary>
    public async Task RecordMonitoringFailureAsync(string runId, Exception ex)
    {
        using var scope = _scopeFactory.CreateScope();
        var runRepository = scope.ServiceProvider.GetRequiredService<IRunRepository>();
        var stateMachine = scope.ServiceProvider.GetRequiredService<RunStateMachine>();

        var run = await runRepository.GetAsync(runId, CancellationToken.None);
        if (run is null || run.Status.IsTerminal())
            return;

        run.ErrorCode = "monitoring_error";
        run.ErrorMessage = ex.Message;
        await stateMachine.TransitionAsync(run, RunStatus.InfraError, "monitoring error", CancellationToken.None);
    }
}
