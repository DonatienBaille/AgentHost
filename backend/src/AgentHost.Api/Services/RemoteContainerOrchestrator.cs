using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using AgentHost.Shared.Contracts;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// L'orchestrateur du mode <c>Runner:Mode = remote</c> : il ne parle à aucun démon, il parle au
/// tier runner en HTTP.
///
/// <para><b>Ce qui débloque réellement les répliques.</b> Ce n'est pas le fait de faire un appel
/// HTTP au lieu d'un appel socket — c'est que le lancement <b>persiste</b> quel runner détient le
/// run (<c>runs.runner_url</c>, migration 0009). L'arrêt et la lecture des journaux relisent cette
/// colonne et routent. Une réplique du backend qui n'a jamais vu ce run sait donc où le trouver, ce
/// qui est exactement ce qui manquait pour dépasser <c>replicaCount: 1</c>.</para>
///
/// <para><b>Ordre d'écriture.</b> L'URL du runner est écrite <em>avant</em> l'appel de lancement,
/// jamais après. Un conteneur démarré dont aucune ligne ne dit où il tourne n'est plus rattrapable
/// autrement qu'en balayant tous les nœuds à la main ; une ligne qui désigne un runner sans
/// conteneur est inoffensive — l'arrêt n'y trouve rien et le confirme.</para>
///
/// <para><b>Les trois cas dégradés, et ce que l'utilisateur en voit.</b>
/// <list type="bullet">
///   <item><b>Colonne nulle</b> (run antérieur à la migration, ou lancé en mode en processus) :
///   l'arrêt renvoie <see cref="StopOutcome.RunnerUnknown"/> et les journaux
///   <c>Retrieved = false</c>. Le run est bien passé en <c>cancelled</c> — l'utilisateur l'a
///   demandé et le laisser courir serait pire — mais l'API répond 202 avec le détail et un
///   événement <c>run.cancel_unconfirmed</c> apparaît sur la chronologie du run.</item>
///   <item><b>Runner injoignable</b> (le pod a disparu) : <see cref="StopOutcome.RunnerUnreachable"/>,
///   même traitement. Le conteneur est très probablement mort avec le nœud, mais « très
///   probablement » n'est pas « confirmé », et c'est cette différence-là qu'il faut afficher.</item>
///   <item><b>Runner joignable mais qui ne connaît pas ce run</b> — cas d'une adresse de pod
///   réattribuée : le runner répond explicitement, et non par un succès vide. Voir
///   <c>RunnerEndpoints.Wait</c>.</item>
/// </list>
/// </para>
/// </summary>
public class RemoteContainerOrchestrator : IContainerOrchestrator
{
    /// <summary>Nom du client HTTP nommé qui porte le jeton porteur et le délai d'expiration.</summary>
    public const string HttpClientName = "agenthost-runner";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly RunnerOptions _options;
    private readonly IRunRepository _runRepository;
    private readonly IAgentManifestParser _manifestParser;
    private readonly RunStateMachine _stateMachine;
    private readonly RunCompletionRecorder _completionRecorder;
    private readonly ILogger _logger;

    public RemoteContainerOrchestrator(
        IHttpClientFactory httpClientFactory,
        RunnerOptions options,
        IRunRepository runRepository,
        IAgentManifestParser manifestParser,
        RunStateMachine stateMachine,
        RunCompletionRecorder completionRecorder,
        ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _runRepository = runRepository;
        _manifestParser = manifestParser;
        _stateMachine = stateMachine;
        _completionRecorder = completionRecorder;
        _logger = logger;
    }

    /// <summary>
    /// Le runner à qui confier un nouveau run. Aujourd'hui : celui de <c>Runner:BaseUrl</c>, c'est-à-dire
    /// le Service du DaemonSet, qui répartit sur les nœuds disponibles. Le choix est isolé ici parce que
    /// c'est le point d'extension d'un ordonnancement réel (placement par charge, par étiquette de nœud,
    /// par file de travaux) — et parce que ce choix n'a d'incidence que sur le lancement : une fois
    /// l'URL persistée, le routage de l'arrêt et des journaux ne dépend plus de lui.
    /// </summary>
    private string SelectRunnerForLaunch() =>
        _options.BaseUrl ?? throw new InvalidOperationException("Runner:BaseUrl is not configured");

    public async Task<string> LaunchAgentAsync(
        Run run, Agent agent, Dictionary<string, string> secrets, CancellationToken ct)
    {
        using var runIdProperty = Serilog.Context.LogContext.PushProperty("RunId", run.Id);

        // Adresse composée pour CHOISIR un runner ; elle est remplacée plus bas par celle que le
        // runner annonce, qui désigne le nœud lui-même.
        var runnerUrl = SelectRunnerForLaunch();

        try
        {
            var spec = ContainerOrchestrator.BuildLaunchSpec(run, agent, secrets, _manifestParser);

            // AVANT le lancement : voir les remarques de classe. Le champ en mémoire est mis à jour
            // en même temps pour que les transitions d'état qui suivent n'écrivent pas un null par-dessus.
            run.RunnerUrl = runnerUrl;
            await _runRepository.SetRunnerUrlAsync(run.Id, runnerUrl, ct);

            using var client = CreateClient();
            using var response = await client.PostAsJsonAsync($"{runnerUrl}/runner/runs", spec, Json, ct);
            await EnsureSuccessAsync(response, runnerUrl, run.Id, ct);

            var launched = await response.Content.ReadFromJsonAsync<RunnerLaunchResponse>(Json, ct)
                ?? throw new InvalidOperationException($"Runner {runnerUrl} returned an empty launch response");

            // Le runner annonce sa propre adresse. C'est elle qu'il faut garder : l'URL composée
            // ci-dessus est celle du Service du DaemonSet, qui répartit sur les nœuds prêts — la
            // conserver reviendrait à demander plus tard l'arrêt de ce conteneur à un nœud tiré au
            // sort. Voir RunnerLaunchResponse.CallbackUrl.
            if (!string.IsNullOrWhiteSpace(launched.CallbackUrl))
            {
                var pinned = launched.CallbackUrl.TrimEnd('/');
                if (!string.Equals(pinned, runnerUrl, StringComparison.Ordinal))
                {
                    run.RunnerUrl = pinned;
                    await _runRepository.SetRunnerUrlAsync(run.Id, pinned, ct);
                    runnerUrl = pinned;
                }
            }

            _logger.Information(
                "Run {RunId} launched on runner {RunnerUrl} ({RunnerId}) as container {ContainerId}",
                run.Id, runnerUrl, launched.RunnerId, launched.ContainerId);

            run.StartedAt = DateTime.UtcNow;
            await _stateMachine.TransitionAsync(run, RunStatus.Running, "container started", ct);

            // Surveillance en arrière-plan : le run survit à la requête HTTP, donc à ses services à
            // portée. Tout ce dont la boucle a besoin est capturé par valeur ; les écritures passent
            // par RunCompletionRecorder, qui ouvre sa propre portée.
            _ = MonitorRemoteRunAsync(runnerUrl, run.Id, run.StartedAt.Value);

            return launched.ContainerId;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to launch agent for run {RunId} on runner {RunnerUrl}", run.Id, runnerUrl);

            run.ErrorCode = "launch_failed";
            run.ErrorMessage = ex.Message;
            await _stateMachine.TransitionAsync(run, RunStatus.InfraError, "launch failed", ct);

            throw;
        }
    }

    /// <summary>
    /// Interroge le runner en attente longue jusqu'à la sortie du conteneur, puis laisse
    /// <see cref="RunCompletionRecorder"/> en tirer les conséquences — les mêmes qu'en mode en
    /// processus, parce que c'est le même code.
    /// </summary>
    private async Task MonitorRemoteRunAsync(string runnerUrl, string runId, DateTime startedAt)
    {
        using var runIdProperty = Serilog.Context.LogContext.PushProperty("RunId", runId);

        var consecutiveFailures = 0;

        while (true)
        {
            try
            {
                using var client = CreateClient(TimeSpan.FromSeconds(_options.WaitTimeoutSeconds + 15));
                using var response = await client.GetAsync(
                    $"{runnerUrl}/runner/runs/{runId}/wait?timeoutSeconds={_options.WaitTimeoutSeconds}",
                    CancellationToken.None);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Le runner répond mais ne connaît pas ce run : il a redémarré, ou l'adresse
                    // désigne désormais un autre pod. Dans les deux cas, personne ne nous dira jamais
                    // comment ce conteneur s'est terminé — le dire est la seule réponse honnête.
                    await FailMonitoringAsync(runId,
                        $"Runner {runnerUrl} no longer knows run {runId} (it restarted, or the address now " +
                        "points at a different node). The run's outcome cannot be observed.");
                    return;
                }

                response.EnsureSuccessStatusCode();

                var outcome = await response.Content.ReadFromJsonAsync<RunnerOutcome>(Json, CancellationToken.None);
                if (outcome is null)
                    throw new InvalidOperationException($"Runner {runnerUrl} returned an empty wait response");

                consecutiveFailures = 0;

                if (!outcome.Exited)
                    continue; // délai d'attente atteint : on repose la question.

                await _completionRecorder.RecordExitAsync(
                    runId, outcome.ExitCode ?? 0, startedAt, outcome.Logs.Length);
                return;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                _logger.Warning(ex,
                    "Waiting on runner {RunnerUrl} for run {RunId} failed ({Failures}/{Max})",
                    runnerUrl, runId, consecutiveFailures, _options.MaxConsecutiveWaitFailures);

                if (consecutiveFailures >= _options.MaxConsecutiveWaitFailures)
                {
                    await FailMonitoringAsync(runId,
                        $"Runner {runnerUrl} stopped answering for run {runId} after " +
                        $"{consecutiveFailures} attempts: {ex.Message}");
                    return;
                }

                // Repli exponentiel plafonné : un runner qui redémarre revient en quelques secondes,
                // et marteler son adresse pendant ce temps n'aide personne.
                var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, consecutiveFailures)));
                await Task.Delay(delay, CancellationToken.None);
            }
        }
    }

    private async Task FailMonitoringAsync(string runId, string message)
    {
        _logger.Error("Run {RunId} monitoring abandoned: {Message}", runId, message);
        await _completionRecorder.RecordMonitoringFailureAsync(runId, new InvalidOperationException(message));
    }

    public async Task<StopResult> StopAsync(string runId, CancellationToken ct)
    {
        var runnerUrl = await _runRepository.GetRunnerUrlAsync(runId, ct);

        if (string.IsNullOrWhiteSpace(runnerUrl))
        {
            var detail =
                $"No runner is recorded for run {runId} (it predates the runner tier, was launched in " +
                "in-process mode, or never reached launch). Nothing was stopped.";
            _logger.Warning("Cannot stop run {RunId}: {Detail}", runId, detail);
            return new StopResult(StopOutcome.RunnerUnknown, detail);
        }

        try
        {
            using var client = CreateClient();
            using var response = await client.PostAsync($"{runnerUrl}/runner/runs/{runId}/stop", content: null, ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = $"Runner {runnerUrl} answered {(int)response.StatusCode} to a stop request for run {runId}.";
                _logger.Warning("Cannot stop run {RunId}: {Detail}", runId, detail);
                return new StopResult(StopOutcome.RunnerUnreachable, detail);
            }

            var stopped = await response.Content.ReadFromJsonAsync<RunnerStopResponse>(Json, ct);

            if (stopped is null)
                return new StopResult(StopOutcome.RunnerUnreachable, $"Runner {runnerUrl} returned an empty stop response.");

            if (!stopped.Confirmed)
            {
                var detail = stopped.Detail ?? $"Runner {runnerUrl} could not confirm that run {runId} is stopped.";
                _logger.Warning("Cannot confirm stop of run {RunId}: {Detail}", runId, detail);
                return new StopResult(StopOutcome.DaemonRefused, detail);
            }

            return StopResult.Ok;
        }
        catch (Exception ex)
        {
            var detail =
                $"Runner {runnerUrl}, which holds run {runId}, is unreachable: {ex.Message}. " +
                "Its container may still be running.";
            _logger.Error(ex, "Cannot stop run {RunId}: runner {RunnerUrl} is unreachable", runId, runnerUrl);
            return new StopResult(StopOutcome.RunnerUnreachable, detail);
        }
    }

    public async Task<RunLogs> GetLogsAsync(string runId, CancellationToken ct)
    {
        var runnerUrl = await _runRepository.GetRunnerUrlAsync(runId, ct);

        if (string.IsNullOrWhiteSpace(runnerUrl))
        {
            return RunLogs.Unavailable(
                $"No runner is recorded for run {runId} (it predates the runner tier, was launched in " +
                "in-process mode, or never reached launch), so there is nowhere to read its logs from.");
        }

        try
        {
            using var client = CreateClient();
            using var response = await client.GetAsync($"{runnerUrl}/runner/runs/{runId}/logs", ct);

            if (!response.IsSuccessStatusCode)
            {
                return RunLogs.Unavailable(
                    $"Runner {runnerUrl} answered {(int)response.StatusCode} when asked for the logs of run {runId}.");
            }

            var logs = await response.Content.ReadFromJsonAsync<RunnerLogsResponse>(Json, ct);

            if (logs is null)
                return RunLogs.Unavailable($"Runner {runnerUrl} returned an empty logs response for run {runId}.");

            return logs.Retrieved
                ? new RunLogs(logs.Logs, true)
                : RunLogs.Unavailable(logs.Detail ?? $"Runner {runnerUrl} has no logs for run {runId}.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Cannot read logs of run {RunId}: runner {RunnerUrl} is unreachable", runId, runnerUrl);
            return RunLogs.Unavailable(
                $"Runner {runnerUrl}, which holds run {runId}, is unreachable: {ex.Message}.");
        }
    }

    private HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = timeout ?? TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
        return client;
    }

    /// <summary>
    /// Transforme une réponse d'erreur du runner en exception portant son corps. Sans le corps, un
    /// « 502 Bad Gateway » côté backend n'apprend rien de ce que le démon a refusé sur le nœud.
    /// </summary>
    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response, string runnerUrl, string runId, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(
            $"Runner {runnerUrl} refused to launch run {runId}: HTTP {(int)response.StatusCode}. {body}");
    }
}
