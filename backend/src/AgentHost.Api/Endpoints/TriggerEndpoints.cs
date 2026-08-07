using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Les déclencheurs entrants (feuille de route, lot 4).
///
/// <b>Deux surfaces, deux régimes d'autorisation, et elles n'ont rien en commun.</b> La gestion
/// (<c>/api/projects/{id}/triggers</c>, <c>/api/triggers/{id}</c>) est authentifiée par JWT et
/// réservée à <c>maintainer</c> : poser un déclencheur, c'est donner à un tiers le droit de
/// dépenser le budget du projet. L'ingestion (<c>/api/hooks/{id}</c>) est <b>anonyme par
/// nécessité</b> — c'est une forge qui appelle, et elle n'a pas de compte ici — et c'est la
/// signature qui autorise.
///
/// <b>Pourquoi l'ingestion lit un tableau d'octets.</b> Le HMAC porte sur les octets exacts reçus.
/// Laisser ASP.NET lier un modèle, puis re-sérialiser pour vérifier, changerait les espaces,
/// l'ordre des clés et l'échappement : toutes les signatures deviendraient fausses. Le corps est
/// donc lu tel quel, une fois, et transmis brut au vérificateur.
///
/// <b>Les réponses ne renseignent pas.</b> Déclencheur inconnu, supprimé, désactivé ou appartenant
/// à un autre locataire : 404 dans les quatre cas. Un 403 confirmerait l'existence de l'objet à qui
/// ne le connaît pas, ce qui suffit à cartographier une installation.
/// </summary>
public static class TriggerEndpoints
{
    /// <summary>Au-delà, ce n'est plus une charge utile de webhook, c'est un envoi de fichier.</summary>
    private const int MaxBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapTriggerEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Triggers").RequireAuthorization();

        api.MapGet("/projects/{projectId}/triggers", ListTriggers).WithName("ListTriggers");
        api.MapPost("/projects/{projectId}/triggers", CreateTrigger).WithName("CreateTrigger")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        api.MapGet("/triggers/{id}", GetTrigger).WithName("GetTrigger");
        api.MapPatch("/triggers/{id}", UpdateTrigger).WithName("UpdateTrigger")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        api.MapDelete("/triggers/{id}", DeleteTrigger).WithName("DeleteTrigger")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);

        // Anonyme : l'émetteur est une forge, pas un utilisateur. Le limiteur global de Program.cs
        // s'y applique comme partout ailleurs, ce qui borne le coût d'un déluge de livraisons
        // invalides.
        app.MapPost("/api/hooks/{triggerId}", IngestDelivery).WithTags("Triggers").WithName("IngestWebhookDelivery")
            .AllowAnonymous();

        return app;
    }

    private static async Task<IResult> ListTriggers(
        string projectId, ITriggerService service, IProjectRepository projects, ICallerContext caller, CancellationToken ct)
    {
        // Le projet est relu dans le périmètre de l'appelant : sans cela, un identifiant de projet
        // étranger rendrait une liste vide au lieu d'un 404, ce qui apprend déjà quelque chose.
        var project = await projects.GetAsync(projectId, ct);
        if (project is null || project.OrgId != caller.OrgId) return Results.NotFound();

        var triggers = await service.ListByProjectAsync(projectId, caller.OrgId, ct);
        return Results.Ok(triggers.Select(TriggerResponse.From).ToList());
    }

    private static async Task<IResult> GetTrigger(
        string id, ITriggerService service, ICallerContext caller, CancellationToken ct)
    {
        var trigger = await service.GetAsync(id, caller.OrgId, ct);
        return trigger is null ? Results.NotFound() : Results.Ok(TriggerResponse.From(trigger));
    }

    private static async Task<IResult> CreateTrigger(
        string projectId,
        CreateTriggerRequest req,
        ITriggerService service,
        ICallerContext caller,
        CancellationToken ct)
    {
        try
        {
            // Le projet de la route et l'organisation du jeton sont tous deux confrontés à l'agent
            // dans le service, avant écriture : un agent qui n'est pas dans ce projet-là, ou pas
            // dans cette organisation-là, n'existe pas du point de vue de cet appel.
            var created = await service.CreateAsync(req, projectId, caller.OrgId, caller.UserId, ct);
            return Results.Created($"/api/triggers/{created.Trigger.Id}", created);
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            // Expression cron illisible, fuseau inconnu, type inconnu : ce sont des erreurs de
            // saisie, et leur message est la seule chose qui permette de les corriger.
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> UpdateTrigger(
        string id, UpdateTriggerRequest req, ITriggerService service, ICallerContext caller, CancellationToken ct)
    {
        try
        {
            var trigger = await service.UpdateAsync(id, req, caller.OrgId, caller.UserId, ct);
            return trigger is null ? Results.NotFound() : Results.Ok(TriggerResponse.From(trigger));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DeleteTrigger(
        string id, ITriggerService service, ICallerContext caller, CancellationToken ct) =>
        await service.DeleteAsync(id, caller.OrgId, caller.UserId, ct)
            ? Results.NoContent()
            : Results.NotFound();

    /// <summary>
    /// Reçoit une livraison d'une forge et, si elle est authentique et concerne ce déclencheur,
    /// lance le run.
    ///
    /// <b>Les codes de retour sont pensés pour l'émetteur, pas pour un humain.</b> GitHub réessaie
    /// sur 5xx et abandonne sur 4xx : une livraison filtrée ou dupliquée doit donc répondre 200 —
    /// elle a été correctement traitée, il n'y a simplement rien à faire — et non 4xx, qui la
    /// ferait apparaître en échec dans l'interface de la forge et inquiéterait pour rien.
    /// </summary>
    private static async Task<IResult> IngestDelivery(
        string triggerId, HttpRequest request, ITriggerService service, CancellationToken ct)
    {
        var body = await ReadBodyAsync(request, ct);
        if (body is null)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var result = await service.IngestAsync(triggerId, body, name => request.Headers[name].FirstOrDefault(), ct);

        return result.Outcome switch
        {
            TriggerIngestOutcome.Launched => Results.Accepted($"/api/runs/{result.RunId}",
                new { status = "launched", runId = result.RunId }),

            // Traitées, sans effet : l'émetteur n'a rien à corriger et rien à réessayer.
            TriggerIngestOutcome.Duplicate => Results.Ok(new { status = "duplicate" }),
            TriggerIngestOutcome.Filtered => Results.Ok(new { status = "filtered" }),

            TriggerIngestOutcome.Unauthorized => Results.Json(
                new { status = "unauthorized", reason = result.Reason }, statusCode: StatusCodes.Status401Unauthorized),

            // 422 et non 500 : la requête est comprise et légitime, c'est son effet qui est
            // impossible (agent sans version publiée, budget mensuel épuisé). Un 5xx ferait
            // réessayer la forge en boucle contre un état qui ne changera pas tout seul.
            TriggerIngestOutcome.Rejected => Results.Json(
                new { status = "rejected", reason = result.Reason },
                statusCode: StatusCodes.Status422UnprocessableEntity),

            _ => Results.NotFound(),
        };
    }

    /// <summary>
    /// Lit le corps brut, ou null s'il dépasse la taille admise.
    ///
    /// Borné explicitement : l'endpoint est anonyme, et rien n'empêcherait sinon d'y déverser un
    /// flux sans fin que le processus garderait en mémoire le temps d'en calculer le HMAC.
    /// </summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentLength > MaxBodyBytes) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
