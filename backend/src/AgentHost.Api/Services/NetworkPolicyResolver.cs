using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Le mode réseau et l'environnement proxy d'un conteneur, dérivés de
/// <c>spec.permissions.network</c>.
/// </summary>
public sealed record NetworkPolicy(
    string Mode, bool HasNetwork, IReadOnlyList<string> EnvVars, IReadOnlyList<string> Allowlist);

/// <summary>
/// La décision « quel réseau pour ce run », isolée de la plomberie Docker.
///
/// C'est de la politique de sécurité, pas de la construction de conteneur : la sortir de
/// l'orchestrateur la rend directement testable, ce qui compte parce que **tous les cas
/// intéressants sont des échecs fermés** — des situations où le manifeste demande un filtrage
/// qu'on ne sait pas appliquer, et où la bonne réponse est de couper le réseau plutôt que
/// d'accorder un accès non filtré.
///
/// Les trois raisons de couper :
/// <list type="bullet">
///   <item>aucun proxy de sortie configuré ;</item>
///   <item>allowlist vide — rien n'est autorisé, donc rien ne doit passer ;</item>
///   <item><b>aucun réseau <c>--internal</c> configuré</b>. C'est le cas le moins évident et le plus
///   important : sur un bridge ordinaire, <c>HTTP_PROXY</c> n'est qu'une convention. Un agent qui
///   ouvre un socket TCP brut, ou une bibliothèque qui ignore la variable, sort sans filtrage.
///   L'« allowlist » n'était donc contraignante que pour le code qui voulait bien coopérer —
///   c'est-à-dire pas pour celui contre lequel elle existe.</item>
/// </list>
///
/// Ce dernier cas se désactive par <c>Docker:AllowUnconfinedAllowlist</c>, parce qu'un déploiement
/// existant qui s'en accommodait perdrait sinon son réseau au premier redémarrage. Le réglage est
/// explicite et journalisé à chaque run : accepter un filtrage de façade doit être un acte conscient.
/// </summary>
public static class NetworkPolicyResolver
{
    /// <param name="EgressProxy">Proxy filtrant pour les runs <c>allowlist</c>. Absent ⇒ pas de réseau.</param>
    /// <param name="AllowlistNetwork">Réseau <c>--internal</c> où seul le proxy est routable.</param>
    /// <param name="NoProxy">Valeur de <c>NO_PROXY</c> transmise au conteneur.</param>
    /// <param name="AllowUnconfined">Accepter un filtrage indicatif faute de réseau interne.</param>
    public sealed record Settings(
        string? EgressProxy,
        string? AllowlistNetwork,
        string NoProxy,
        bool AllowUnconfined);

    private static readonly string[] NoEnv = [];
    private static readonly string[] NoHosts = [];

    /// <summary>Réseau coupé : aucune interface, quelle que soit la raison.</summary>
    private static NetworkPolicy NoNetwork(IReadOnlyList<string> allowlist) =>
        new("none", false, NoEnv, allowlist);

    public static NetworkPolicy Resolve(
        string? requested,
        IReadOnlyList<string> allowlist,
        string runId,
        Settings settings,
        ILogger logger)
    {
        switch (requested)
        {
            case "full":
                return new NetworkPolicy("bridge", true, NoEnv, NoHosts);

            case "allowlist" when string.IsNullOrWhiteSpace(settings.EgressProxy):
                // Prétendre qu'« allowlist » est satisfaite par un bridge non filtré serait
                // strictement pire que pas de réseau : l'auteur du manifeste a demandé un filtrage.
                logger.Warning(
                    "Run {RunId} requests network=allowlist but Docker:EgressProxy is not configured; " +
                    "starting the container with NO network rather than granting unfiltered egress",
                    runId);
                return NoNetwork(allowlist);

            case "allowlist" when allowlist.Count == 0:
                logger.Warning(
                    "Run {RunId} requests network=allowlist but spec.permissions.networkAllowlist is empty; " +
                    "starting the container with NO network (nothing is allowed)",
                    runId);
                return NoNetwork(allowlist);

            case "allowlist" when string.IsNullOrWhiteSpace(settings.AllowlistNetwork)
                                  && !settings.AllowUnconfined:
                // Même raisonnement que pour le proxy absent : sur un bridge ordinaire le filtrage
                // n'est qu'indicatif, donc l'accorder revient à mentir sur ce qui est appliqué.
                logger.Warning(
                    "Run {RunId} requests network=allowlist but Docker:AllowlistNetwork is not configured; " +
                    "on an ordinary bridge HTTP_PROXY is advisory only, so the container gets NO network. " +
                    "Create an --internal network reaching only the proxy, or set " +
                    "Docker:AllowUnconfinedAllowlist=true to accept advisory-only filtering.",
                    runId);
                return NoNetwork(allowlist);

            case "allowlist":
            {
                var joined = string.Join(",", allowlist);
                var env = new[]
                {
                    $"HTTP_PROXY={settings.EgressProxy}",
                    $"http_proxy={settings.EgressProxy}",
                    $"HTTPS_PROXY={settings.EgressProxy}",
                    $"https_proxy={settings.EgressProxy}",
                    $"NO_PROXY={settings.NoProxy}",
                    $"no_proxy={settings.NoProxy}",
                    // Informational: lets a cooperating agent (and the proxy tier, which can read
                    // the matching agenthost.network_allowlist label) see the policy it runs under.
                    $"AGENTHOST_NETWORK_ALLOWLIST={joined}",
                };

                if (string.IsNullOrWhiteSpace(settings.AllowlistNetwork))
                {
                    // On y est parce que l'exploitant l'a explicitement demandé. Le redire à chaque
                    // run, pas seulement au démarrage : c'est la trace qui restera dans le journal
                    // le jour où l'on cherchera comment un agent a joint un hôte non autorisé.
                    logger.Warning(
                        "Run {RunId} runs with advisory-only egress filtering (Docker:AllowUnconfinedAllowlist " +
                        "is on and no internal network is configured): anything ignoring HTTP_PROXY reaches the " +
                        "network unfiltered", runId);
                }

                logger.Information(
                    "Run {RunId} egress restricted to {AllowlistCount} host(s) via proxy {Proxy} on network {Network}",
                    runId, allowlist.Count, settings.EgressProxy, settings.AllowlistNetwork ?? "bridge");

                return new NetworkPolicy(settings.AllowlistNetwork ?? "bridge", true, env, allowlist);
            }

            default:
                // Inclut "none" et toute valeur inconnue : un mode qu'on ne comprend pas n'accorde
                // rien. Une faute de frappe dans un manifeste ne doit pas ouvrir le réseau.
                return NoNetwork(NoHosts);
        }
    }
}
