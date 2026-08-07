using System.Text.Json;
using AgentHost.Api.Domain;
using AgentHost.Shared.Containers;
using AgentHost.Shared.Contracts;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Ce qu'un run peut apprendre d'une demande d'arrêt de son conteneur.
///
/// <para>L'ancienne signature <c>Task StopAsync(...)</c> avalait toutes les exceptions et ne
/// renvoyait rien : une annulation qui n'avait rien arrêté était indiscernable d'une annulation
/// réussie, et l'utilisateur voyait « annulé » pendant que l'agent continuait de consommer du
/// budget. Avec un tier runner le cas devient courant (le pod qui détenait le run a disparu), donc
/// le résultat est désormais explicite.</para>
/// </summary>
public enum StopOutcome
{
    /// <summary>Le nœud confirme qu'aucun conteneur de ce run n'y tourne plus.</summary>
    Confirmed,

    /// <summary>Aucun runner n'est enregistré pour ce run (run antérieur au tier runner, ou lancé en mode inprocess).</summary>
    RunnerUnknown,

    /// <summary>Le runner enregistré n'a pas répondu, ou ne connaît plus ce run.</summary>
    RunnerUnreachable,

    /// <summary>Le démon de conteneurs a refusé l'arrêt.</summary>
    DaemonRefused,
}

/// <param name="Outcome">Ce qui s'est réellement passé.</param>
/// <param name="Detail">Message technique à journaliser et à remonter tel quel à l'appelant.</param>
public sealed record StopResult(StopOutcome Outcome, string? Detail = null)
{
    /// <summary>Vrai seulement si le conteneur est bel et bien arrêté. Tout le reste est un doute.</summary>
    public bool Confirmed => Outcome == StopOutcome.Confirmed;

    public static readonly StopResult Ok = new(StopOutcome.Confirmed);
}

/// <param name="Content">Le texte des journaux. Vide quand <paramref name="Retrieved"/> est faux.</param>
/// <param name="Retrieved">
/// Faux quand les journaux n'ont pas pu être obtenus. Le champ existe pour que l'API puisse répondre
/// « je ne sais pas » au lieu de renvoyer un message d'erreur dans le champ <c>logs</c>, où il se lit
/// comme la sortie de l'agent.
/// </param>
public sealed record RunLogs(string Content, bool Retrieved, string? Detail = null)
{
    public static RunLogs Unavailable(string detail) => new(string.Empty, false, detail);
}

public interface IContainerOrchestrator
{
    Task<string> LaunchAgentAsync(Run run, Agent agent, Dictionary<string, string> secrets, CancellationToken ct);
    Task<StopResult> StopAsync(string runId, CancellationToken ct);
    Task<RunLogs> GetLogsAsync(string runId, CancellationToken ct);
}

/// <summary>
/// Orchestrateur en processus (spec section 7.1) : le mode par défaut, et le comportement
/// historique. Le backend parle au démon de conteneurs de son propre nœud.
///
/// <para>Depuis le lot 1.1, la plomberie conteneur elle-même vit dans
/// <see cref="ContainerLauncher"/> (projet AgentHost.Shared), partagée telle quelle avec le tier
/// runner : durcissement du HostConfig, livraison des secrets par fichiers, politique réseau,
/// attente, journaux, suppression. Ce qui reste ici est ce que le runner n'a pas le droit de
/// connaître : la base, la machine à états et le bus d'événements.</para>
///
/// <para>La limite que ce mode ne franchit pas, et qui a motivé le tier runner :
/// <see cref="StopAsync"/> et <see cref="GetLogsAsync"/> cherchent le conteneur sur *ce* démon.
/// Une seconde réplique du backend ne trouve donc rien pour un run lancé par la première. C'est
/// pourquoi <c>Runner:Mode = inprocess</c> reste correct pour une réplique unique, et seulement
/// pour elle.</para>
/// </summary>
public class ContainerOrchestrator : IContainerOrchestrator
{
    private readonly ContainerLauncher _launcher;
    private readonly IAgentManifestParser _manifestParser;
    private readonly RunStateMachine _stateMachine;
    private readonly RunCompletionRecorder _completionRecorder;
    private readonly ILogger _logger;

    public ContainerOrchestrator(
        ContainerLauncher launcher,
        IAgentManifestParser manifestParser,
        RunStateMachine stateMachine,
        RunCompletionRecorder completionRecorder,
        ILogger logger)
    {
        _launcher = launcher;
        _manifestParser = manifestParser;
        _stateMachine = stateMachine;
        _completionRecorder = completionRecorder;
        _logger = logger;
    }

    /// <summary>
    /// Traduit un run + son agent en <see cref="AgentLaunchSpec"/> : l'analyse du manifeste et la
    /// composition de l'environnement se font une seule fois, ici, et servent aux deux modes.
    /// </summary>
    internal static AgentLaunchSpec BuildLaunchSpec(
        Run run, Agent agent, Dictionary<string, string> secrets, IAgentManifestParser manifestParser)
    {
        var manifest = manifestParser.Parse(agent.ManifestYaml);
        var containerPolicy = manifestParser.ParseContainerPolicy(agent.ManifestYaml);

        var envVars = new List<string>
        {
            $"AGENTHOST_RUN_ID={run.Id}",
            $"AGENTHOST_PROJECT_ID={run.ProjectId}",
            $"AGENTHOST_INPUTS={JsonSerializer.Serialize(run.Inputs)}",
            "AGENTHOST_PROTOCOL_VERSION=1.0",
            // Identifiant de rappel du protocole agent, propre au run (docs/agent-protocol.md) ;
            // frappé dans RunService juste avant le lancement, jamais persisté.
            $"AGENTHOST_RUN_TOKEN={run.AgentRunToken}",
        };

        // NOTE : les secrets ne sont volontairement PAS ajoutés à envVars — ils ne sont livrés que
        // sous forme de fichiers dans /run/secrets (voir ContainerLauncher et docs/agent-protocol.md).

        if (agent.Type != AgentType.Oci && manifest.Spec.External?.Config is { } externalConfig)
        {
            foreach (var (key, value) in externalConfig)
                envVars.Add($"AGENT_{key.ToUpperInvariant()}={value}");
        }

        return new AgentLaunchSpec
        {
            RunId = run.Id,
            ProjectId = run.ProjectId,
            AgentId = run.AgentId,
            RunNumber = run.Number,
            ImageRef = agent.ImageRef ?? $"agenthost/{agent.Type.ToDbString()}:latest",
            Env = envVars,
            Network = manifest.Spec.Permissions.Network,
            NetworkAllowlist = containerPolicy.NetworkAllowlist,
            WritableRootfs = containerPolicy.WritableRootfs,
            CpuCores = run.RuntimeProfile.Cpu,
            MemoryBytes = run.RuntimeProfile.MemoryBytes,
            MaxDurationSeconds = run.RuntimeProfile.MaxDurationSeconds,
            Secrets = secrets,
        };
    }

    public async Task<string> LaunchAgentAsync(
        Run run,
        Agent agent,
        Dictionary<string, string> secrets,
        CancellationToken ct)
    {
        // Toute ligne de journal émise pendant le lancement/la surveillance de ce run porte RunId
        // (spec 14.1), y compris celles venant d'appels imbriqués qui ne reçoivent pas le run.
        using var runIdProperty = Serilog.Context.LogContext.PushProperty("RunId", run.Id);

        try
        {
            var spec = BuildLaunchSpec(run, agent, secrets, _manifestParser);
            var containerId = await _launcher.CreateAndStartAsync(spec, ct);

            run.StartedAt = DateTime.UtcNow;
            await _stateMachine.TransitionAsync(run, RunStatus.Running, "container started", ct);

            // Surveillance en arrière-plan dans une portée DI neuve : la portée de la requête HTTP
            // appelante (et ses dépôts/connexions à portée) peut être libérée bien avant la fin du
            // conteneur, un run pouvant survivre des heures à la requête qui l'a créé.
            _ = MonitorContainerAsync(containerId, run.Id, run.StartedAt.Value);

            return containerId;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to launch agent for run {RunId}", run.Id);

            run.ErrorCode = "launch_failed";
            run.ErrorMessage = ex.Message;
            // InfraError (et non Failed) parce que cela peut arriver alors que run.Status vaut
            // encore Preparing, et que Failed n'est atteignable que depuis Finalizing (spec 8.2).
            await _stateMachine.TransitionAsync(run, RunStatus.InfraError, "launch failed", ct);

            throw;
        }
    }

    private async Task MonitorContainerAsync(string containerId, string runId, DateTime startedAt)
    {
        using var runIdProperty = Serilog.Context.LogContext.PushProperty("RunId", runId);

        try
        {
            var exitCode = await _launcher.WaitAsync(containerId, CancellationToken.None);

            _logger.Information("Container {ContainerId} exited with code {ExitCode}", containerId, exitCode);

            // Première chose une fois le conteneur parti : détruire les secrets en clair. Tout ce
            // qui suit (collecte des journaux, transitions d'état) peut échouer sans les laisser.
            _launcher.DeleteRunSecrets(runId);

            string logContent;
            try
            {
                logContent = await _launcher.CollectLogsAsync(containerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to collect logs for container {ContainerId}", containerId);
                logContent = string.Empty;
            }

            try
            {
                await _launcher.RemoveAsync(containerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to remove container {ContainerId}", containerId);
            }

            await _completionRecorder.RecordExitAsync(runId, exitCode, startedAt, logContent.Length, containerId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error monitoring container {ContainerId}", containerId);
            await _completionRecorder.RecordMonitoringFailureAsync(runId, ex);
        }
        finally
        {
            // Filet pour tous les chemins qui n'atteignent pas la suppression post-sortie ci-dessus
            // (attente en échec, plantage entre les deux, ...). Idempotent.
            _launcher.DeleteRunSecrets(runId);
        }
    }

    public async Task<StopResult> StopAsync(string runId, CancellationToken ct)
    {
        var response = await _launcher.StopAsync(runId, ct);
        return response.Confirmed
            ? StopResult.Ok
            : new StopResult(StopOutcome.DaemonRefused, response.Detail);
    }

    public async Task<RunLogs> GetLogsAsync(string runId, CancellationToken ct)
    {
        var response = await _launcher.GetLogsAsync(runId, ct);
        return response.Retrieved
            ? new RunLogs(response.Logs, true)
            : RunLogs.Unavailable(response.Detail ?? $"No logs available for run {runId}");
    }
}
