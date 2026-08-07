using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Services;

namespace AgentHost.Api.Endpoints;

/// <summary>
/// Consultation du journal d'audit (feuille de route, lot 3).
///
/// <b>Ce qui a changé.</b> Le journal était servi par une liste paginée sans filtre ni total :
/// techniquement complet, pratiquement inutilisable. On ne consulte pas un journal d'audit par
/// curiosité — on le consulte parce que quelque chose s'est produit, et la question a toujours la
/// même forme : « qui a fait quoi, sur quoi, entre quand et quand ». Ces quatre dimensions sont
/// désormais des filtres appliqués en SQL, et les valeurs qu'elles peuvent prendre sont servies par
/// <c>/facets</c> plutôt que devinées par le client.
///
/// <b>Isolation.</b> Le <c>{orgId}</c> de la route est conservé pour la forme de l'URL mais n'est
/// pas une source : il doit correspondre à l'organisation du JWT, sinon la ressource est déclarée
/// inexistante. Le filtre exécuté porte <c>caller.OrgId</c>, jamais la valeur de la route. Lire le
/// journal d'un autre locataire exposerait ses identifiants d'utilisateurs, ses ressources et son
/// activité — c'est-à-dire à peu près tout ce que l'isolation protège ailleurs.
/// </summary>
public static class AuditEndpoints
{
    /// <summary>Taille de page par défaut. Une page de journal se lit, elle ne se télécharge pas.</summary>
    private const int DefaultTake = 50;

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/organizations/{orgId}/audit-log", ListAuditLog).WithTags("Audit").WithName("ListAuditLog")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        app.MapGet("/api/organizations/{orgId}/audit-log/facets", AuditFacetsEndpoint).WithTags("Audit").WithName("AuditLogFacets")
            .RequireAuthorization(AuthorizationPolicies.Maintainer);
        return app;
    }

    /// <summary>
    /// Une page filtrée du journal, avec le total du jeu filtré.
    ///
    /// La réponse est une enveloppe et non un tableau : sans total, une pagination ne peut ni
    /// annoncer « 1–50 sur 812 » ni savoir qu'elle est arrivée au bout autrement qu'en demandant
    /// une page vide.
    /// </summary>
    private static async Task<IResult> ListAuditLog(
        string orgId,
        IAuditService auditService,
        ICallerContext caller,
        CancellationToken ct,
        string? action = null,
        string? actorUserId = null,
        string? resourceType = null,
        string? resourceId = null,
        DateTime? from = null,
        DateTime? to = null,
        int skip = 0,
        int take = DefaultTake)
    {
        if (!caller.BelongsToCallerOrg(orgId)) return Results.NotFound();

        var page = await auditService.SearchAsync(new AuditQuery
        {
            OrgId = caller.OrgId,
            Action = action,
            ActorUserId = actorUserId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            From = ToUtc(from),
            To = ToUtc(to),
            Skip = skip,
            Take = take,
        }, ct);

        return Results.Ok(page);
    }

    /// <summary>Les valeurs présentes dans ce journal : actions, types de ressource, acteurs, ancienneté.</summary>
    private static async Task<IResult> AuditFacetsEndpoint(
        string orgId,
        IAuditService auditService,
        ICallerContext caller,
        CancellationToken ct)
    {
        if (!caller.BelongsToCallerOrg(orgId)) return Results.NotFound();
        return Results.Ok(await auditService.GetFacetsAsync(caller.OrgId, ct));
    }

    /// <summary>
    /// Ramène une borne de période en UTC.
    ///
    /// <c>created_at</c> est un <c>TIMESTAMP</c> sans fuseau, alimenté par <c>DateTime.UtcNow</c> :
    /// la base contient de l'UTC, sans le dire. Une borne reçue en heure locale (<c>+02:00</c>)
    /// comparée telle quelle décalerait le filtre de deux heures — assez pour qu'une entrée
    /// manque à l'appel sans que personne ne s'en aperçoive. Une borne sans fuseau est prise pour
    /// de l'UTC, ce qui est la seule lecture cohérente avec ce qui est stocké.
    /// </summary>
    private static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } => value,
        { Kind: DateTimeKind.Local } => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
    };
}
