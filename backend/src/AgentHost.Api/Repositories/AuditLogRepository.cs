using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>Write-once, read-many audit trail (audit_log table). No update/delete by design.</summary>
public interface IAuditLogRepository
{
    Task InsertAsync(AuditLogEntry entry, CancellationToken ct = default);
    Task<List<AuditLogEntry>> ListByOrgAsync(string orgId, int skip = 0, int take = 100, CancellationToken ct = default);
    Task<List<AuditLogEntry>> ListByResourceAsync(string orgId, string resourceType, string resourceId, CancellationToken ct = default);

    /// <summary>Consultation filtrée du journal, avec le total du jeu filtré (feuille de route, lot 3).</summary>
    Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct = default);

    /// <summary>Ce que le journal de cette organisation contient réellement, pour construire les filtres.</summary>
    Task<AuditFacets> GetFacetsAsync(string orgId, CancellationToken ct = default);
}

public class AuditLogRepository : IAuditLogRepository
{
    private const string SelectColumns = """
        id, org_id, action, actor_user_id, resource_type, resource_id,
        changes, details, created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public AuditLogRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InsertAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO audit_log (id, org_id, action, actor_user_id, resource_type, resource_id, changes, details, created_at)
            VALUES (@Id, @OrgId, @Action, @ActorUserId, @ResourceType, @ResourceId, @Changes::jsonb, @Details::jsonb, @CreatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, entry, cancellationToken: ct));
        _logger.Information("Audit log: {Action} on {ResourceType} {ResourceId} by {ActorUserId}",
            entry.Action, entry.ResourceType, entry.ResourceId, entry.ActorUserId);
    }

    public async Task<List<AuditLogEntry>> ListByOrgAsync(string orgId, int skip = 0, int take = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM audit_log
            WHERE org_id = @OrgId
            ORDER BY created_at DESC
            LIMIT @Take OFFSET @Skip
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<AuditLogEntry>(new CommandDefinition(
            sql,
            new { OrgId = orgId, Skip = Paging.ClampSkip(skip), Take = Paging.ClampTake(take) },
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<AuditLogEntry>> ListByResourceAsync(string orgId, string resourceType, string resourceId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM audit_log
            WHERE org_id = @OrgId AND resource_type = @ResourceType AND resource_id = @ResourceId
            ORDER BY created_at DESC
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<AuditLogEntry>(new CommandDefinition(sql, new { OrgId = orgId, ResourceType = resourceType, ResourceId = resourceId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// Consultation filtrée (feuille de route, lot 3).
    ///
    /// <b>Le WHERE est construit, pas paramétré en bloc.</b> La forme habituelle
    /// <c>(@Action IS NULL OR action = @Action)</c> tient en une constante mais coûte les index :
    /// le planificateur ne peut pas savoir à la construction du plan quelles branches seront
    /// actives, et retombe volontiers sur un parcours séquentiel. On n'ajoute donc que les
    /// prédicats réellement demandés. Aucune donnée d'appelant n'entre dans la chaîne SQL — seuls
    /// des fragments littéraux écrits ici — et les valeurs restent des paramètres nommés.
    ///
    /// <b>Le total vient d'une fenêtre, pas d'une seconde requête.</b> <c>COUNT(*) OVER()</c> est
    /// évalué sur le jeu filtré avant le LIMIT : une seule lecture donne la page et son total, là
    /// où deux requêtes pourraient déjà diverger si une entrée s'insérait entre les deux — ce qui,
    /// sur un journal qu'on n'écrit qu'en ajout, arrive précisément pendant qu'on le consulte.
    /// Le prix est que Postgres parcourt tout le jeu filtré ; c'est le prix d'un total exact, et
    /// l'index <c>(org_id, …, created_at DESC)</c> le maintient à un parcours d'index.
    /// </summary>
    public async Task<AuditPage> SearchAsync(AuditQuery query, CancellationToken ct = default)
    {
        var skip = Paging.ClampSkip(query.Skip);
        var take = Paging.ClampTake(query.Take);

        var parameters = new DynamicParameters();
        parameters.Add("OrgId", query.OrgId);
        parameters.Add("Skip", skip);
        parameters.Add("Take", take);

        // L'organisation d'abord, et jamais conditionnelle : c'est la frontière du locataire.
        var predicates = new List<string> { "a.org_id = @OrgId" };

        void Filter(string? value, string predicate, string name)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            predicates.Add(predicate);
            parameters.Add(name, value);
        }

        Filter(query.Action, "a.action = @Action", "Action");
        Filter(query.ActorUserId, "a.actor_user_id = @ActorUserId", "ActorUserId");
        Filter(query.ResourceType, "a.resource_type = @ResourceType", "ResourceType");
        Filter(query.ResourceId, "a.resource_id = @ResourceId", "ResourceId");

        if (query.From is not null)
        {
            predicates.Add("a.created_at >= @From");
            parameters.Add("From", query.From);
        }

        if (query.To is not null)
        {
            // Borne haute exclue : « le 3 mars » se demande [03-03, 04-03[ sans avoir à écrire
            // 23:59:59.999 et sans perdre les entrées de la dernière milliseconde.
            predicates.Add("a.created_at < @To");
            parameters.Add("To", query.To);
        }

        var sql = $"""
            SELECT a.id, a.action, a.actor_user_id, a.resource_type, a.resource_id,
                   a.changes, a.details, a.created_at,
                   u.email        AS actor_email,
                   u.display_name AS actor_display_name,
                   COUNT(*) OVER() AS total_count
            FROM audit_log a
            LEFT JOIN users u ON u.id = a.actor_user_id AND u.org_id = a.org_id
            WHERE {string.Join(" AND ", predicates)}
            ORDER BY a.created_at DESC, a.id DESC
            LIMIT @Take OFFSET @Skip
            """;

        using var db = _connectionFactory.CreateConnection();
        var rows = (await db.QueryAsync<SearchRow>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();

        return new AuditPage
        {
            // Zéro ligne ⇒ aucune fenêtre à lire ⇒ total nul, ce qui est la bonne réponse.
            Total = rows.Count > 0 ? rows[0].TotalCount : 0,
            Skip = skip,
            Take = take,
            Items = rows.Select(r => new AuditEntryView
            {
                Id = r.Id,
                Action = r.Action,
                ActorUserId = r.ActorUserId,
                ActorEmail = r.ActorEmail,
                ActorDisplayName = r.ActorDisplayName,
                ResourceType = r.ResourceType,
                ResourceId = r.ResourceId,
                Changes = r.Changes,
                Details = r.Details,
                CreatedAt = r.CreatedAt,
            }).ToList(),
        };
    }

    /// <summary>
    /// Les valeurs réellement présentes dans le journal d'une organisation.
    ///
    /// Trois agrégats et une borne, en un seul aller-retour : ils sont toujours lus ensemble, et
    /// les séparer multiplierait par quatre le coût d'ouverture d'une page qui n'affiche que des
    /// listes déroulantes.
    /// </summary>
    public async Task<AuditFacets> GetFacetsAsync(string orgId, CancellationToken ct = default)
    {
        // Les acteurs sont plafonnés : une organisation de plusieurs milliers de comptes ne rentre
        // pas dans une liste déroulante, et les plus actifs sont ceux qu'on cherche. Les actions et
        // les types de ressource, eux, forment un vocabulaire fermé côté serveur — quelques
        // dizaines de valeurs au plus, jamais fonction de la taille de l'organisation.
        const string sql = """
            SELECT action AS value, COUNT(*) AS count
            FROM audit_log WHERE org_id = @OrgId
            GROUP BY action ORDER BY COUNT(*) DESC, action;

            SELECT resource_type AS value, COUNT(*) AS count
            FROM audit_log WHERE org_id = @OrgId AND resource_type IS NOT NULL
            GROUP BY resource_type ORDER BY COUNT(*) DESC, resource_type;

            SELECT a.actor_user_id AS user_id,
                   MAX(u.email)         AS email,
                   MAX(u.display_name)  AS display_name,
                   COUNT(*)             AS count
            FROM audit_log a
            LEFT JOIN users u ON u.id = a.actor_user_id AND u.org_id = a.org_id
            WHERE a.org_id = @OrgId AND a.actor_user_id IS NOT NULL
            GROUP BY a.actor_user_id
            ORDER BY COUNT(*) DESC, a.actor_user_id
            LIMIT @ActorLimit;

            SELECT MIN(created_at) AS earliest, COUNT(*) AS total
            FROM audit_log WHERE org_id = @OrgId;
            """;

        using var db = _connectionFactory.CreateConnection();
        using var multi = await db.QueryMultipleAsync(new CommandDefinition(
            sql, new { OrgId = orgId, ActorLimit = MaxActorFacets }, cancellationToken: ct));

        var actions = (await multi.ReadAsync<AuditFacetValue>()).ToList();
        var resourceTypes = (await multi.ReadAsync<AuditFacetValue>()).ToList();
        var actors = (await multi.ReadAsync<AuditActorFacet>()).ToList();
        var span = await multi.ReadSingleAsync<SpanRow>();

        return new AuditFacets
        {
            Actions = actions,
            ResourceTypes = resourceTypes,
            Actors = actors,
            EarliestEntry = span.Earliest,
            TotalEntries = span.Total,
        };
    }

    /// <summary>Combien d'acteurs au plus dans les facettes. Au-delà, ce n'est plus une liste de choix.</summary>
    private const int MaxActorFacets = 100;

    private sealed class SearchRow
    {
        public string Id { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string? ActorUserId { get; set; }
        public string? ActorEmail { get; set; }
        public string? ActorDisplayName { get; set; }
        public string? ResourceType { get; set; }
        public string? ResourceId { get; set; }
        public System.Text.Json.Nodes.JsonNode? Changes { get; set; }
        public System.Text.Json.Nodes.JsonNode? Details { get; set; }
        public DateTime CreatedAt { get; set; }
        public int TotalCount { get; set; }
    }

    private sealed class SpanRow
    {
        public DateTime? Earliest { get; set; }
        public int Total { get; set; }
    }
}
