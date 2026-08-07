using System.Text.Json.Nodes;

namespace AgentHost.Api.Domain;

/// <summary>Nature d'un déclencheur : ce qui le fait partir.</summary>
public enum TriggerType
{
    Webhook,
    Cron,
}

public static class TriggerTypeExtensions
{
    public static string ToDbString(this TriggerType type) => type switch
    {
        TriggerType.Webhook => "webhook",
        TriggerType.Cron => "cron",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static TriggerType FromDbString(string value) => value switch
    {
        "webhook" => TriggerType.Webhook,
        "cron" => TriggerType.Cron,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown trigger type"),
    };
}

/// <summary>
/// L'émetteur d'un webhook entrant, qui détermine comment sa signature se vérifie.
///
/// Ce n'est pas une préférence esthétique : chaque forge signe autrement. GitHub calcule un HMAC
/// SHA-256 sur le corps brut et le place dans <c>X-Hub-Signature-256</c> ; GitLab n'en calcule
/// aucun et envoie le secret en clair dans <c>X-Gitlab-Token</c>. Accepter l'un pour l'autre
/// reviendrait à accepter un jeton porteur là où on attend une preuve de possession.
/// </summary>
public enum TriggerProvider
{
    GitHub,
    GitLab,
    Generic,
}

public static class TriggerProviderExtensions
{
    public static string ToDbString(this TriggerProvider provider) => provider switch
    {
        TriggerProvider.GitHub => "github",
        TriggerProvider.GitLab => "gitlab",
        TriggerProvider.Generic => "generic",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static TriggerProvider FromDbString(string value) => value switch
    {
        "github" => TriggerProvider.GitHub,
        "gitlab" => TriggerProvider.GitLab,
        "generic" => TriggerProvider.Generic,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown trigger provider"),
    };
}

/// <summary>
/// Ce qu'un déclencheur webhook accepte de laisser passer.
///
/// <b>Un filtre vide laisse tout passer</b>, et c'est le défaut. L'inverse — un filtre vide qui ne
/// laisse rien passer — donnerait un déclencheur silencieux dont personne ne comprendrait
/// l'inaction : l'IHM afficherait « actif », la forge afficherait « livré », et rien ne partirait.
/// </summary>
public sealed class TriggerEventFilter
{
    /// <summary>Types d'événement acceptés (<c>push</c>, <c>pull_request</c>…). Vide = tous.</summary>
    public List<string> Events { get; set; } = [];

    /// <summary>
    /// Branches acceptées, motif <c>*</c> autorisé en suffixe (<c>release/*</c>). Vide = toutes.
    /// Ignoré pour une livraison qui ne porte pas de référence.
    /// </summary>
    public List<string> Branches { get; set; } = [];
}

/// <summary>
/// Un déclencheur : la règle qui fait partir un run sans que personne ne clique.
///
/// Webhook et cron partagent tout sauf trois colonnes — la cible, les entrées, l'activation — d'où
/// une seule table et un seul modèle, avec les champs propres à chaque nature laissés nuls pour
/// l'autre. La base garantit la cohérence par <c>triggers_shape_check</c> (migration 0011) plutôt
/// que de s'en remettre à une validation applicative.
/// </summary>
public class Trigger
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;

    public TriggerType Type { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Entrées injectées dans chaque run. Un déclencheur n'a personne devant lui pour remplir un
    /// formulaire : ce qu'il ne porte pas ici, le run ne l'aura pas.
    /// </summary>
    public JsonNode? Inputs { get; set; }

    // ---- webhook ----

    /// <summary>Secret HMAC chiffré. Il ne quitte jamais le serveur, y compris vers l'IHM.</summary>
    public byte[]? SecretEncrypted { get; set; }

    public TriggerProvider? Provider { get; set; }
    public TriggerEventFilter? EventFilter { get; set; }

    // ---- cron ----

    public string? CronExpression { get; set; }
    public string TimeZone { get; set; } = "UTC";

    /// <summary>Prochaine échéance, en UTC. C'est la colonne que le planificateur interroge.</summary>
    public DateTime? NextRunAt { get; set; }

    public DateTime? LastRunAt { get; set; }
    public string? LastRunId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
