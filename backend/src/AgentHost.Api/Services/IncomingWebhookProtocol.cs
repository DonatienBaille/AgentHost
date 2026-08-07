using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentHost.Api.Domain;

namespace AgentHost.Api.Services;

/// <summary>Ce qu'une vérification de signature a conclu.</summary>
public enum SignatureVerdict
{
    /// <summary>La livraison prouve la possession du secret.</summary>
    Valid,

    /// <summary>Aucune signature fournie : la requête n'a même pas tenté de s'authentifier.</summary>
    Missing,

    /// <summary>Signature présente et fausse. C'est le cas qui mérite d'être journalisé.</summary>
    Invalid,
}

/// <summary>
/// Le protocole des webhooks entrants : vérifier une livraison, l'identifier, et décider si elle
/// concerne ce déclencheur (feuille de route, lot 4).
///
/// <b>Pourquoi trois émetteurs et pas un format maison unique.</b> On ne choisit pas comment GitHub
/// signe. Il calcule un HMAC-SHA256 sur le corps <b>brut</b> et le pose dans
/// <c>X-Hub-Signature-256</c> ; GitLab ne signe rien du tout et envoie le secret en clair dans
/// <c>X-Gitlab-Token</c>. Exiger une signature de GitLab reviendrait à ne jamais accepter ses
/// livraisons ; accepter un jeton en clair de GitHub reviendrait à dégrader une preuve de
/// possession en jeton porteur. Le format « generic » existe pour tout le reste et suit la
/// convention GitHub, qui est la plus sûre des trois.
///
/// <b>Le corps brut, et jamais un corps re-sérialisé.</b> Le HMAC porte sur les octets exacts
/// reçus : reparser puis réécrire le JSON change les espaces, l'ordre des clés et l'échappement
/// Unicode, et toutes les signatures deviendraient fausses. C'est pour cette raison que
/// l'endpoint d'ingestion lit un <c>byte[]</c> et ne se fait pas lier un modèle.
///
/// <b>Comparaison à temps constant.</b> Un <c>==</c> sur chaînes s'arrête au premier octet qui
/// diffère : le temps de réponse dévoilerait alors, octet par octet, la signature attendue.
/// </summary>
public static class IncomingWebhookProtocol
{
    public const string GitHubSignatureHeader = "X-Hub-Signature-256";
    public const string GitHubEventHeader = "X-GitHub-Event";
    public const string GitHubDeliveryHeader = "X-GitHub-Delivery";

    public const string GitLabTokenHeader = "X-Gitlab-Token";
    public const string GitLabEventHeader = "X-Gitlab-Event";
    public const string GitLabDeliveryHeader = "X-Gitlab-Event-UUID";

    public const string GenericSignatureHeader = "X-AgentHost-Signature-256";
    public const string GenericEventHeader = "X-AgentHost-Event";
    public const string GenericDeliveryHeader = "X-AgentHost-Delivery";

    /// <summary>Vérifie une livraison contre le secret du déclencheur.</summary>
    public static SignatureVerdict Verify(
        TriggerProvider provider, string secret, byte[] body, Func<string, string?> header)
    {
        return provider switch
        {
            TriggerProvider.GitHub => VerifyHmac(header(GitHubSignatureHeader), secret, body),
            TriggerProvider.Generic => VerifyHmac(header(GenericSignatureHeader), secret, body),
            TriggerProvider.GitLab => VerifyToken(header(GitLabTokenHeader), secret),
            _ => SignatureVerdict.Invalid,
        };
    }

    private static SignatureVerdict VerifyHmac(string? presented, string secret, byte[] body)
    {
        if (string.IsNullOrWhiteSpace(presented)) return SignatureVerdict.Missing;

        // Le préfixe `sha256=` fait partie du format ; l'absence de préfixe est une signature
        // d'un autre algorithme (GitHub envoie aussi un `X-Hub-Signature` en SHA-1, que l'on
        // refuse délibérément — il est cassé et son en-tête n'est plus qu'un vestige).
        const string prefix = "sha256=";
        if (!presented.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return SignatureVerdict.Invalid;

        var hex = presented[prefix.Length..];
        if (hex.Length != 64) return SignatureVerdict.Invalid;

        byte[] presentedBytes;
        try
        {
            presentedBytes = Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return SignatureVerdict.Invalid;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, presentedBytes)
            ? SignatureVerdict.Valid
            : SignatureVerdict.Invalid;
    }

    private static SignatureVerdict VerifyToken(string? presented, string secret)
    {
        if (string.IsNullOrWhiteSpace(presented)) return SignatureVerdict.Missing;

        // Comparaison à temps constant malgré tout : le jeton est en clair, mais sa découverte
        // octet par octet resterait une découverte.
        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(secret);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b)
            ? SignatureVerdict.Valid
            : SignatureVerdict.Invalid;
    }

    /// <summary>
    /// L'identifiant de la livraison, pour la déduplication.
    ///
    /// À défaut d'en-tête — un émetteur générique n'est pas tenu d'en fournir — on retombe sur
    /// l'empreinte du corps. Deux livraisons au contenu rigoureusement identique sont alors
    /// indiscernables et traitées comme un doublon : mieux vaut ne pas relancer un run qu'en
    /// facturer deux pour le même événement.
    /// </summary>
    public static string DeliveryId(TriggerProvider provider, byte[] body, Func<string, string?> header)
    {
        var declared = provider switch
        {
            TriggerProvider.GitHub => header(GitHubDeliveryHeader),
            TriggerProvider.GitLab => header(GitLabDeliveryHeader),
            _ => header(GenericDeliveryHeader),
        };

        if (!string.IsNullOrWhiteSpace(declared)) return Truncate(declared);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
    }

    /// <summary>Le type d'événement annoncé, ou null si l'émetteur n'en déclare pas.</summary>
    public static string? EventName(TriggerProvider provider, Func<string, string?> header) => provider switch
    {
        TriggerProvider.GitHub => header(GitHubEventHeader),
        TriggerProvider.GitLab => header(GitLabEventHeader),
        _ => header(GenericEventHeader),
    };

    /// <summary>
    /// La branche visée, déduite de la charge utile.
    ///
    /// GitHub comme GitLab publient <c>ref</c> sous la forme <c>refs/heads/main</c> sur un push.
    /// Une livraison sans <c>ref</c> (ouverture d'une issue, commentaire) n'a pas de branche, et le
    /// filtre de branches ne s'y applique pas plutôt que de la rejeter.
    /// </summary>
    public static string? BranchOf(JsonNode? payload)
    {
        var reference = payload?["ref"]?.GetValue<string>();
        if (string.IsNullOrEmpty(reference)) return null;

        const string headsPrefix = "refs/heads/";
        return reference.StartsWith(headsPrefix, StringComparison.Ordinal)
            ? reference[headsPrefix.Length..]
            : reference;
    }

    /// <summary>
    /// Cette livraison passe-t-elle le filtre du déclencheur ?
    ///
    /// Une liste vide laisse tout passer. L'inverse donnerait un déclencheur muet dont personne ne
    /// comprendrait l'inaction : l'IHM dirait « actif », la forge dirait « livré », rien ne
    /// partirait.
    /// </summary>
    public static bool Accepts(TriggerEventFilter? filter, string? eventName, string? branch)
    {
        if (filter is null) return true;

        if (filter.Events.Count > 0)
        {
            if (eventName is null) return false;
            if (!filter.Events.Any(e => string.Equals(e, eventName, StringComparison.OrdinalIgnoreCase)))
                return false;
        }

        // Pas de branche dans la charge utile ⇒ le filtre de branches ne s'applique pas. Une
        // livraison d'issue n'est pas « sur une mauvaise branche », elle est hors sujet pour ce
        // critère-là.
        if (filter.Branches.Count > 0 && branch is not null)
            return filter.Branches.Any(pattern => MatchesBranch(pattern, branch));

        return true;
    }

    /// <summary>Correspondance de branche : égalité, ou motif à joker terminal (<c>release/*</c>).</summary>
    private static bool MatchesBranch(string pattern, string branch)
    {
        if (pattern == "*") return true;

        if (pattern.EndsWith('*'))
            return branch.StartsWith(pattern[..^1], StringComparison.Ordinal);

        return string.Equals(pattern, branch, StringComparison.Ordinal);
    }

    /// <summary>La colonne fait 128 caractères ; un en-tête hostile n'a pas à faire échouer l'insertion.</summary>
    private static string Truncate(string value) => value.Length <= 128 ? value : value[..128];
}
