using System.Text.Json.Nodes;

namespace AgentHost.Api.Domain;

/// <summary>
/// Un filtre de consultation du journal d'audit (feuille de route, lot 3).
///
/// <b>L'organisation n'est pas un filtre, c'est une frontière.</b> <see cref="OrgId"/> vient
/// toujours du JWT de l'appelant et jamais de la requête : les autres propriétés restreignent ce
/// qu'on voit à l'intérieur de son organisation, celle-ci délimite ce qui existe. C'est pourquoi
/// elle est requise là où tout le reste est facultatif.
///
/// Un champ nul signifie « pas de contrainte », pas « valeur nulle » : filtrer sur les entrées
/// système (acteur NULL) n'est pas exprimable ici, et c'est délibéré — la question qu'on pose à un
/// journal d'audit est « qui a fait ça », pas « qu'est-ce que personne n'a fait ».
/// </summary>
public sealed record AuditQuery
{
    public required string OrgId { get; init; }

    /// <summary>Action exacte (<c>secret.created</c>). Les valeurs présentes sont servies par les facettes.</summary>
    public string? Action { get; init; }

    public string? ActorUserId { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }

    /// <summary>Borne basse, incluse. UTC.</summary>
    public DateTime? From { get; init; }

    /// <summary>Borne haute, <b>exclue</b> : une journée se demande [J, J+1[ sans se soucier des millisecondes.</summary>
    public DateTime? To { get; init; }

    public int Skip { get; init; }
    public int Take { get; init; } = 50;
}

/// <summary>
/// Une entrée du journal telle qu'elle est servie à l'IHM : la ligne brute, plus l'identité de son
/// acteur.
///
/// <b>Pourquoi joindre l'acteur ici plutôt que de laisser l'IHM le faire.</b> La table ne stocke
/// qu'un ULID. Affiché tel quel — ce que faisait la page — il n'apprend rien : personne ne
/// reconnaît <c>01HZX…</c>. L'IHM ne peut pas résoudre ces identifiants elle-même sans lire la
/// liste des utilisateurs, ce qui demande un rôle qu'elle n'a pas forcément et ne retrouverait de
/// toute façon pas les comptes supprimés.
///
/// Les comptes supprimés sont justement la raison pour laquelle la jointure ignore
/// <c>deleted_at</c> : le sens d'un journal d'audit est de rester lisible après coup, et un acteur
/// dont le compte a été supprimé est exactement celui qu'on cherche à identifier.
/// </summary>
public sealed class AuditEntryView
{
    public string Id { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;

    public string? ActorUserId { get; set; }
    public string? ActorEmail { get; set; }
    public string? ActorDisplayName { get; set; }

    public string? ResourceType { get; set; }
    public string? ResourceId { get; set; }

    public JsonNode? Changes { get; set; }
    public JsonNode? Details { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Une page de résultats, avec le total du jeu filtré.
///
/// <b>Le total n'est pas décoratif.</b> Sans lui, l'IHM ne peut ni afficher « 1–50 sur 812 » ni
/// savoir s'il existe une page suivante autrement qu'en la demandant. C'est ce qui distingue une
/// pagination d'une liste qu'on fait défiler à l'aveugle.
/// </summary>
public sealed class AuditPage
{
    public List<AuditEntryView> Items { get; set; } = [];

    /// <summary>Nombre d'entrées correspondant au filtre, toutes pages confondues.</summary>
    public int Total { get; set; }

    public int Skip { get; set; }
    public int Take { get; set; }
}

/// <summary>Une valeur présente dans le journal, et son volume.</summary>
public sealed class AuditFacetValue
{
    public string Value { get; set; } = string.Empty;
    public int Count { get; set; }
}

/// <summary>Un acteur ayant laissé au moins une trace, avec de quoi l'afficher.</summary>
public sealed class AuditActorFacet
{
    public string UserId { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? DisplayName { get; set; }
    public int Count { get; set; }
}

/// <summary>
/// Ce que le journal d'une organisation contient réellement, servi pour construire les filtres.
///
/// <b>Pourquoi des facettes et non un champ de saisie libre.</b> Les actions sont un vocabulaire
/// fermé côté serveur (<c>run.created</c>, <c>secret.rotated</c>, …) mais nulle part documenté côté
/// client. Un champ libre oblige à deviner l'orthographe exacte, et une faute de frappe rend un
/// journal vide qu'on lit comme « il ne s'est rien passé ». Les facettes suppriment la question :
/// on ne propose que ce qui existe, avec le nombre d'entrées derrière chaque valeur.
/// </summary>
public sealed class AuditFacets
{
    public List<AuditFacetValue> Actions { get; set; } = [];
    public List<AuditFacetValue> ResourceTypes { get; set; } = [];
    public List<AuditActorFacet> Actors { get; set; } = [];

    /// <summary>Date de la plus ancienne entrée, ou null si le journal est vide. Borne les sélecteurs de période.</summary>
    public DateTime? EarliestEntry { get; set; }

    public int TotalEntries { get; set; }
}
