using AgentHost.Shared.Contracts;
using Serilog;

namespace AgentHost.Runner;

/// <summary>
/// L'API du runner. Quatre verbes, aucun état métier, aucun tenant : le nœud ne sait pas ce qu'est
/// une organisation, un projet ou un budget, et n'a rien à autoriser au-delà du jeton porteur.
/// C'est délibéré — toute autorisation dupliquée ici serait une seconde source de vérité à tenir en
/// phase avec le backend.
///
/// <para>Mappé par une méthode publique plutôt que directement dans <c>Program.cs</c> pour que les
/// tests puissent héberger la <b>vraie</b> API du runner devant un lanceur simulé, au lieu de tester
/// une imitation de runner écrite pour l'occasion.</para>
/// </summary>
public static class RunnerEndpoints
{
    /// <summary>Délai maximal d'une attente longue, borné pour ne pas dépasser les délais d'inactivité des ingress.</summary>
    private const int MaxWaitSeconds = 120;

    /// <summary>
    /// Adresse propre de ce pod runner (<c>Runner:AdvertisedUrl</c>), renvoyée à chaque lancement.
    /// Voir <see cref="RunnerLaunchResponse.CallbackUrl"/> : c'est elle que le backend persiste, et
    /// non l'adresse du Service par laquelle il est arrivé.
    /// </summary>
    private static string? _advertisedUrl;

    public static IEndpointRouteBuilder MapRunnerEndpoints(
        this IEndpointRouteBuilder app, string authToken, string? advertisedUrl = null)
    {
        _advertisedUrl = string.IsNullOrWhiteSpace(advertisedUrl) ? null : advertisedUrl.Trim().TrimEnd('/');

        var runner = app.MapGroup("/runner").RequireRunnerToken(authToken);

        runner.MapPost("/runs", Launch).WithName("RunnerLaunch");
        runner.MapPost("/runs/{runId}/stop", Stop).WithName("RunnerStop");
        runner.MapGet("/runs/{runId}/logs", Logs).WithName("RunnerLogs");
        runner.MapGet("/runs/{runId}/wait", Wait).WithName("RunnerWait");

        return app;
    }

    private static async Task<IResult> Launch(
        AgentLaunchSpec spec, RunSupervisor supervisor, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(spec.RunId) || string.IsNullOrWhiteSpace(spec.ImageRef))
            return Results.BadRequest(new { error = "runId and imageRef are required" });

        using var runIdProperty = Serilog.Context.LogContext.PushProperty("RunId", spec.RunId);

        try
        {
            var containerId = await supervisor.LaunchAsync(spec, ct);
            return Results.Ok(new RunnerLaunchResponse
            {
                ContainerId = containerId,
                RunnerId = RunSupervisor.RunnerId,
                CallbackUrl = _advertisedUrl,
            });
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Failed to launch run {RunId} on this node", spec.RunId);

            // 502 et non 500 : ce qui a échoué est l'appel au démon de conteneurs, en aval du
            // runner. La distinction dit au backend s'il doit réessayer ailleurs (502) ou si sa
            // requête était mauvaise (400).
            return Results.Json(
                new { error = ex.Message, runnerId = RunSupervisor.RunnerId },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> Stop(string runId, RunSupervisor supervisor, CancellationToken ct)
    {
        var response = await supervisor.StopAsync(runId, ct);
        return Results.Ok(response);
    }

    private static async Task<IResult> Logs(string runId, RunSupervisor supervisor, CancellationToken ct)
    {
        var response = await supervisor.GetLogsAsync(runId, ct);
        return Results.Ok(response);
    }

    /// <summary>
    /// Attente longue de l'issue du conteneur. <b>404 quand ce nœud ne connaît pas ce run</b> : c'est
    /// la réponse qui compte le plus de toute cette API. Une adresse de pod peut être réattribuée à
    /// un autre nœud ; sans ce 404, le backend interrogerait un runner étranger et prendrait son
    /// « rien à signaler » pour « le run tourne toujours ».
    /// </summary>
    private static async Task<IResult> Wait(
        string runId, RunSupervisor supervisor, CancellationToken ct, int timeoutSeconds = 30)
    {
        if (!supervisor.Knows(runId))
        {
            return Results.Json(
                new { error = "unknown_run", runId, runnerId = RunSupervisor.RunnerId },
                statusCode: StatusCodes.Status404NotFound);
        }

        var seconds = Math.Clamp(timeoutSeconds, 1, MaxWaitSeconds);

        try
        {
            var outcome = await supervisor.WaitAsync(runId, TimeSpan.FromSeconds(seconds), ct);
            if (outcome is null)
            {
                return Results.Json(
                    new { error = "unknown_run", runId, runnerId = RunSupervisor.RunnerId },
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.Ok(outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Le client est parti ; rien à dire. 499 « client closed request » : hors RFC mais
            // compris par les journaux d'accès, et surtout jamais confondu avec un succès.
            return Results.StatusCode(499);
        }
        catch (Exception ex)
        {
            // La surveillance elle-même a échoué : le backend doit le voir comme une erreur
            // d'infrastructure, pas comme « toujours en cours ».
            return Results.Json(
                new { error = ex.Message, runId, runnerId = RunSupervisor.RunnerId },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
