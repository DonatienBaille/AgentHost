using System.Collections.Concurrent;
using AgentHost.Shared.Containers;
using AgentHost.Shared.Contracts;
using Serilog;

namespace AgentHost.Runner;

/// <summary>
/// Ce que le nœud sait de ses propres conteneurs, et pourquoi il doit le savoir tout seul.
///
/// <para>La surveillance d'un conteneur — attendre sa sortie, effacer les secrets en clair,
/// récupérer les journaux, supprimer le conteneur — est du travail nœud-local et doit se faire même
/// si le backend qui a demandé le lancement disparaît entre-temps. C'est le superviseur qui la
/// porte, pas le backend : sinon un redémarrage de réplique backend laisserait des secrets en clair
/// sur disque et des conteneurs morts non nettoyés sur le nœud.</para>
///
/// <para>Ce qui reste au backend, c'est la <b>conséquence</b> de la sortie : le code de sortie, les
/// transitions d'état, l'événement <c>run.finished</c>. Il l'obtient en interrogeant
/// <c>GET /runner/runs/{runId}/wait</c>, une attente longue qui répond immédiatement si l'issue est
/// déjà connue — ce qui rend le backend librement redémarrable : il repose la question.</para>
///
/// <para><b>Rétention.</b> Les issues sont gardées en mémoire pendant
/// <c>Runner:OutcomeRetentionMinutes</c> (60 par défaut) pour que le backend puisse encore les lire
/// après le redémarrage du conteneur, et pour que les journaux d'un run terminé restent lisibles
/// alors que le conteneur, lui, a été supprimé. En mémoire et non en base : le runner n'a pas de
/// base, et cette information est reconstructible (le backend fait foi une fois qu'il l'a
/// enregistrée). Un redémarrage du pod runner la perd — c'est traité explicitement côté backend,
/// qui voit alors « runner injoignable / run inconnu » plutôt qu'un faux succès.</para>
/// </summary>
public sealed class RunSupervisor
{
    private sealed record Tracked(
        string ContainerId,
        DateTime StartedAt,
        TaskCompletionSource<RunnerOutcome> Completion)
    {
        public DateTime? SettledAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, Tracked> _runs = new(StringComparer.Ordinal);
    private readonly ContainerLauncher _launcher;
    private readonly ILogger _logger;
    private readonly TimeSpan _retention;

    public RunSupervisor(ContainerLauncher launcher, IConfiguration config, ILogger logger)
    {
        _launcher = launcher;
        _logger = logger;

        var minutes = int.TryParse(config["Runner:OutcomeRetentionMinutes"], out var m) && m > 0 ? m : 60;
        _retention = TimeSpan.FromMinutes(minutes);
    }

    /// <summary>Identité de ce processus runner, telle qu'elle apparaît dans les réponses et les journaux.</summary>
    public static string RunnerId { get; } = $"{Environment.MachineName}/{Environment.ProcessId}";

    /// <summary>
    /// Lance le conteneur du run et prend en charge sa surveillance. Renvoie l'identifiant du
    /// conteneur dès qu'il est démarré : l'appelant n'attend pas la fin du run.
    /// </summary>
    public async Task<string> LaunchAsync(AgentLaunchSpec spec, CancellationToken ct)
    {
        var containerId = await _launcher.CreateAndStartAsync(spec, ct);

        PruneSettled();

        var tracked = new Tracked(
            containerId,
            DateTime.UtcNow,
            new TaskCompletionSource<RunnerOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));

        // Un relancement du même run écrase l'entrée précédente ; l'ancienne attente est close pour
        // ne pas laisser un appelant suspendu sur un conteneur dont plus personne ne parle.
        if (_runs.TryGetValue(spec.RunId, out var previous))
            previous.Completion.TrySetResult(new RunnerOutcome { Exited = false });

        _runs[spec.RunId] = tracked;

        _ = MonitorAsync(spec.RunId, tracked);
        return containerId;
    }

    /// <summary>
    /// Attend l'issue du conteneur, au plus <paramref name="timeout"/>. Renvoie
    /// <c>Exited = false</c> quand le délai est atteint : c'est une réponse valide, à laquelle
    /// l'appelant répond en redemandant. Renvoie <c>null</c> quand ce nœud ne connaît pas ce run —
    /// cas qu'il ne faut surtout pas confondre avec « toujours en cours ».
    /// </summary>
    public async Task<RunnerOutcome?> WaitAsync(string runId, TimeSpan timeout, CancellationToken ct)
    {
        if (!_runs.TryGetValue(runId, out var tracked))
            return null;

        if (tracked.Completion.Task.IsCompleted)
            return await tracked.Completion.Task;

        var finished = await Task.WhenAny(tracked.Completion.Task, Task.Delay(timeout, ct));
        return finished == tracked.Completion.Task
            ? await tracked.Completion.Task
            : new RunnerOutcome { Exited = false };
    }

    /// <summary>
    /// Arrête le conteneur du run sur ce nœud.
    ///
    /// <para>La subtilité est le cas « rien arrêté ». Le démon peut répondre « aucun conteneur
    /// portant cette étiquette » pour deux raisons opposées : (a) ce nœud a bien tenu ce run et le
    /// conteneur est déjà sorti — il n'y a rien à arrêter, tout va bien ; (b) ce nœud n'a jamais
    /// entendu parler de ce run, parce que l'adresse enregistrée par le backend désigne désormais un
    /// autre pod. Confondre les deux, c'est répondre « annulé » pour un conteneur qui tourne encore
    /// ailleurs. La trace en mémoire du superviseur les sépare.</para>
    ///
    /// <para>Le cas (a) reste correct après un redémarrage du runner : le conteneur porte
    /// l'étiquette, il est donc retrouvé et arrêté, et l'arrêt est confirmé sans que la mémoire ait
    /// eu à survivre.</para>
    /// </summary>
    public async Task<RunnerStopResponse> StopAsync(string runId, CancellationToken ct)
    {
        var response = await _launcher.StopAsync(runId, ct);

        if (!response.Confirmed || response.Stopped > 0 || Knows(runId))
            return response;

        return response with
        {
            Confirmed = false,
            Detail =
                $"This node has no record of run {runId} and no container carrying its label. " +
                "It was never launched here, or this runner has restarted since. Nothing was stopped.",
        };
    }

    /// <summary>
    /// Journaux du run. Le conteneur est demandé au démon en premier ; s'il a déjà été supprimé
    /// mais que ce nœud a gardé l'issue, ce sont les journaux capturés à la sortie qui sont rendus.
    /// Le mode en processus, lui, ne peut que répondre « aucun conteneur trouvé » dans ce cas.
    /// </summary>
    public async Task<RunnerLogsResponse> GetLogsAsync(string runId, CancellationToken ct)
    {
        var live = await _launcher.GetLogsAsync(runId, ct);
        if (live.Retrieved)
            return live;

        if (_runs.TryGetValue(runId, out var tracked)
            && tracked.Completion.Task.IsCompletedSuccessfully)
        {
            var outcome = await tracked.Completion.Task;
            if (outcome.Exited)
                return new RunnerLogsResponse { Logs = outcome.Logs, Retrieved = true };
        }

        return live;
    }

    /// <summary>Vrai si ce nœud a lancé ce run et en garde encore trace.</summary>
    public bool Knows(string runId) => _runs.ContainsKey(runId);

    private async Task MonitorAsync(string runId, Tracked tracked)
    {
        using var runIdProperty = Serilog.Context.LogContext.PushProperty("RunId", runId);

        try
        {
            var exitCode = await _launcher.WaitAsync(tracked.ContainerId, CancellationToken.None);
            _logger.Information("Container {ContainerId} exited with code {ExitCode}", tracked.ContainerId, exitCode);

            // Première chose une fois le conteneur parti : détruire les secrets en clair. Tout ce
            // qui suit peut échouer sans les laisser derrière.
            _launcher.DeleteRunSecrets(runId);

            string logs;
            try
            {
                logs = await _launcher.CollectLogsAsync(tracked.ContainerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to collect logs for container {ContainerId}", tracked.ContainerId);
                logs = string.Empty;
            }

            try
            {
                await _launcher.RemoveAsync(tracked.ContainerId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to remove container {ContainerId}", tracked.ContainerId);
            }

            tracked.SettledAt = DateTime.UtcNow;
            tracked.Completion.TrySetResult(new RunnerOutcome
            {
                Exited = true,
                ExitCode = exitCode,
                Logs = logs,
                FinishedAt = tracked.SettledAt,
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error monitoring container {ContainerId}", tracked.ContainerId);
            tracked.SettledAt = DateTime.UtcNow;
            // L'attente du backend reçoit l'exception : le run part alors en infra_error, ce qui est
            // exactement ce qu'il faut dire quand le nœud ne sait plus ce qu'est devenu le conteneur.
            tracked.Completion.TrySetException(ex);
            // Marque l'exception comme observée : personne n'attend forcément cette tâche, et une
            // exception de tâche non observée est un bruit de finaliseur sans rapport avec le sujet.
            _ = tracked.Completion.Task.ContinueWith(
                t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }
        finally
        {
            // Filet pour tous les chemins qui n'atteignent pas la suppression ci-dessus. Idempotent.
            _launcher.DeleteRunSecrets(runId);
        }
    }

    /// <summary>
    /// Oublie les runs terminés depuis plus longtemps que la rétention. Appelé à chaque lancement
    /// plutôt que par une minuterie : le seul moment où la table grossit est un lancement, et cela
    /// évite un service d'arrière-plan de plus dans un processus qu'on veut minuscule.
    /// </summary>
    private void PruneSettled()
    {
        var cutoff = DateTime.UtcNow - _retention;
        foreach (var (runId, tracked) in _runs)
        {
            if (tracked.SettledAt is { } settled && settled < cutoff)
                _runs.TryRemove(runId, out _);
        }
    }
}
