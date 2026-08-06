namespace AgentHost.Shared.Contracts;

/// <summary>
/// Tout ce dont un nœud a besoin pour lancer le conteneur d'un run, et rien de plus.
///
/// <para>C'est le contrat exact entre « décider » (le backend : quel agent, quel manifeste, quels
/// secrets, quel budget) et « exécuter » (le nœud : créer et démarrer un conteneur durci). Le
/// manifeste YAML n'apparaît pas ici : il est analysé une seule fois, côté backend, et ce qui en
/// sort est déjà réduit à des valeurs. Un runner n'a donc jamais à comprendre le format des
/// manifestes, ce qui lui retire une surface d'analyse et évite qu'une divergence de version
/// d'analyseur entre les deux tiers change silencieusement la politique appliquée.</para>
///
/// <para><b>Les secrets voyagent dans ce message.</b> En mode <c>remote</c> ils traversent donc le
/// réseau entre le backend et le runner, en clair dans le corps HTTP. C'est assumé et documenté :
/// le lien backend → runner est un lien interne au cluster, authentifié par
/// <c>Runner:AuthToken</c>, et doit être servi en TLS (ou sur un maillage qui le chiffre) dès que
/// « interne » ne veut pas dire « le même nœud ». Voir docs/runner.md.</para>
/// </summary>
public sealed record AgentLaunchSpec
{
    public required string RunId { get; init; }
    public required string ProjectId { get; init; }
    public required string AgentId { get; init; }

    /// <summary>Numéro du run dans son projet ; sert de nom d'hôte au conteneur.</summary>
    public long RunNumber { get; init; }

    /// <summary>Référence d'image complète, déjà résolue (jamais un alias à interpréter côté nœud).</summary>
    public required string ImageRef { get; init; }

    /// <summary>
    /// Variables d'environnement <c>NOM=valeur</c> construites par le backend (identifiants du run,
    /// entrées, jeton de rappel du protocole agent, configuration d'agent externe). Les variables
    /// liées à la politique réseau sont ajoutées par le nœud, qui est seul à savoir quel proxy et
    /// quel réseau existent chez lui.
    /// </summary>
    public IReadOnlyList<string> Env { get; init; } = Array.Empty<string>();

    /// <summary>Valeur brute de <c>spec.permissions.network</c> : <c>none</c>, <c>full</c>, <c>allowlist</c>.</summary>
    public string? Network { get; init; }

    /// <summary>Hôtes de <c>spec.permissions.networkAllowlist</c>.</summary>
    public IReadOnlyList<string> NetworkAllowlist { get; init; } = Array.Empty<string>();

    /// <summary>Opt-in explicite du manifeste (<c>spec.permissions.writableRootfs</c>).</summary>
    public bool WritableRootfs { get; init; }

    public long CpuCores { get; init; }
    public long MemoryBytes { get; init; }
    public int MaxDurationSeconds { get; init; }

    /// <summary>
    /// Secrets à écrire sous <c>/run/secrets/&lt;NOM&gt;</c>. Volontairement jamais injectés en
    /// variables d'environnement — voir les remarques de <c>ContainerLauncher</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> Secrets { get; init; } = new Dictionary<string, string>();
}

/// <summary>Réponse à <c>POST /runner/runs</c>.</summary>
public sealed record RunnerLaunchResponse
{
    public required string ContainerId { get; init; }

    /// <summary>
    /// Identité du processus runner qui détient ce conteneur (nom d'hôte du pod + identifiant de
    /// processus). Purement informative : ce que le backend persiste pour router, c'est l'URL. Elle
    /// sert à rendre lisible un journal du type « le runner qui détenait ce run n'est plus celui
    /// qui répond à cette adresse ».
    /// </summary>
    public string? RunnerId { get; init; }
}

/// <summary>
/// Issue d'un conteneur, telle que le nœud l'observe. <see cref="Exited"/> à faux signifie
/// « toujours en cours » : c'est la réponse d'une attente longue qui a atteint son délai, pas une
/// erreur.
/// </summary>
public sealed record RunnerOutcome
{
    public bool Exited { get; init; }
    public long? ExitCode { get; init; }
    public string Logs { get; init; } = string.Empty;
    public DateTime? FinishedAt { get; init; }
}

/// <summary>Réponse à <c>POST /runner/runs/{runId}/stop</c>.</summary>
public sealed record RunnerStopResponse
{
    /// <summary>Nombre de conteneurs effectivement arrêtés (0 = il n'y en avait plus).</summary>
    public int Stopped { get; init; }

    /// <summary>
    /// Vrai quand le nœud a la certitude qu'aucun conteneur de ce run n'y tourne plus — soit parce
    /// qu'il en a arrêté un, soit parce qu'il n'en a trouvé aucun. Faux uniquement si le démon a
    /// refusé l'arrêt : le backend doit alors dire à l'utilisateur que l'annulation n'est pas
    /// confirmée plutôt que d'afficher « annulé ».
    /// </summary>
    public bool Confirmed { get; init; }

    public string? Detail { get; init; }
}

/// <summary>Réponse à <c>GET /runner/runs/{runId}/logs</c>.</summary>
public sealed record RunnerLogsResponse
{
    public string Logs { get; init; } = string.Empty;

    /// <summary>
    /// Faux quand le nœud n'a pas pu produire les journaux (conteneur inconnu, démon en erreur).
    /// Le texte de <see cref="Logs"/> n'est alors PAS un journal d'agent et ne doit jamais être
    /// présenté comme tel.
    /// </summary>
    public bool Retrieved { get; init; }

    public string? Detail { get; init; }
}
