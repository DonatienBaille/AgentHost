using System.Text.Json.Nodes;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

/// <summary>
/// Création d'un déclencheur (feuille de route, lot 4).
///
/// Il n'y a ni <c>projectId</c> ni <c>orgId</c> ici, et c'est la règle du dépôt : l'agent
/// appartient à exactement un projet, qui appartient à exactement une organisation. Les faire
/// passer par la requête créerait deux valeurs qui peuvent se contredire, et la contradiction est
/// exactement ce qu'un attaquant cherche.
/// </summary>
public class CreateTriggerRequest
{
    public string AgentId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    /// <summary><c>webhook</c> ou <c>cron</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Entrées fixes du run déclenché. Un déclencheur n'a personne pour remplir un formulaire.</summary>
    public JsonNode? Inputs { get; set; }

    // ---- webhook ----

    /// <summary><c>github</c>, <c>gitlab</c> ou <c>generic</c>. Détermine la vérification de signature.</summary>
    public string? Provider { get; set; }

    /// <summary>Types d'événement acceptés. Vide = tous.</summary>
    public List<string>? Events { get; set; }

    /// <summary>Branches acceptées, joker terminal autorisé. Vide = toutes.</summary>
    public List<string>? Branches { get; set; }

    // ---- cron ----

    /// <summary>Expression à cinq champs (<c>0 9 * * 1-5</c>).</summary>
    public string? CronExpression { get; set; }

    /// <summary>Fuseau d'interprétation (<c>Europe/Paris</c>). UTC par défaut.</summary>
    public string? TimeZone { get; set; }
}

public class UpdateTriggerRequest
{
    public string? Name { get; set; }
    public bool? IsActive { get; set; }
    public JsonNode? Inputs { get; set; }
    public List<string>? Events { get; set; }
    public List<string>? Branches { get; set; }
    public string? CronExpression { get; set; }
    public string? TimeZone { get; set; }
}

/// <summary>
/// Un déclencheur tel qu'il est servi.
///
/// <b>Le secret n'y figure pas.</b> Il n'est rendu qu'une fois, à la création, dans
/// <see cref="CreateTriggerResponse"/> : un secret qu'une API relit à volonté n'est plus protégé
/// par le chiffrement au repos, il est protégé par l'autorisation de lecture — ce qui est un cran
/// plus faible et un cran plus facile à perdre.
/// </summary>
public class TriggerResponse
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public JsonNode? Inputs { get; set; }

    public string? Provider { get; set; }
    public List<string> Events { get; set; } = [];
    public List<string> Branches { get; set; } = [];

    /// <summary>Chemin à configurer chez l'émetteur. Relatif : l'hôte public n'est pas connu d'ici.</summary>
    public string? WebhookPath { get; set; }

    public string? CronExpression { get; set; }
    public string TimeZone { get; set; } = "UTC";
    public DateTime? NextRunAt { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunId { get; set; }

    public DateTime CreatedAt { get; set; }

    public static TriggerResponse From(Trigger t) => new()
    {
        Id = t.Id,
        ProjectId = t.ProjectId,
        AgentId = t.AgentId,
        Type = t.Type.ToDbString(),
        Name = t.Name,
        IsActive = t.IsActive,
        Inputs = t.Inputs,
        Provider = t.Provider?.ToDbString(),
        Events = t.EventFilter?.Events ?? [],
        Branches = t.EventFilter?.Branches ?? [],
        WebhookPath = t.Type == TriggerType.Webhook ? $"/api/hooks/{t.Id}" : null,
        CronExpression = t.CronExpression,
        TimeZone = t.TimeZone,
        NextRunAt = t.NextRunAt,
        LastRunAt = t.LastRunAt,
        LastRunId = t.LastRunId,
        CreatedAt = t.CreatedAt,
    };
}

/// <summary>La réponse de création : le déclencheur, plus le secret, qui ne repassera plus.</summary>
public class CreateTriggerResponse
{
    public TriggerResponse Trigger { get; set; } = new();

    /// <summary>
    /// Le secret à coller chez l'émetteur. Rendu une seule fois : il n'est stocké que chiffré, et
    /// aucune lecture ultérieure ne le redonne.
    /// </summary>
    public string? Secret { get; set; }
}

/// <summary>Ce qu'une livraison entrante a produit.</summary>
public enum TriggerIngestOutcome
{
    /// <summary>Signature valide, filtre passé, run lancé.</summary>
    Launched,

    /// <summary>Déclencheur inconnu, supprimé ou désactivé. Répondu 404, jamais 403.</summary>
    UnknownTrigger,

    /// <summary>Signature absente ou fausse.</summary>
    Unauthorized,

    /// <summary>Livraison déjà traitée : réémission, on ne relance pas.</summary>
    Duplicate,

    /// <summary>Livraison authentique mais hors du filtre. Ce n'est pas une erreur.</summary>
    Filtered,

    /// <summary>Le run n'a pas pu être créé (agent sans version publiée, budget épuisé…).</summary>
    Rejected,
}

public class TriggerIngestResult
{
    public TriggerIngestOutcome Outcome { get; set; }
    public string? RunId { get; set; }

    /// <summary>Raison lisible d'un refus, destinée à l'émetteur — jamais au détail près.</summary>
    public string? Reason { get; set; }
}
