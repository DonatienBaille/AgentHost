using Microsoft.Extensions.Configuration;

namespace AgentHost.Api.Services;

/// <summary>Où le backend exécute les conteneurs.</summary>
public enum RunnerMode
{
    /// <summary>
    /// Le backend parle au démon de son propre nœud. <b>Le défaut, et le comportement historique
    /// mot pour mot.</b> Correct pour une réplique unique, et pour elle seule : une seconde réplique
    /// ne trouverait pas les conteneurs lancés par la première.
    /// </summary>
    InProcess,

    /// <summary>
    /// Le backend délègue à AgentHost.Runner, et persiste quel runner détient chaque run
    /// (<c>runs.runner_url</c>). C'est ce qui rend le backend réplicable.
    /// </summary>
    Remote,
}

/// <summary>
/// Section <c>Runner:</c> côté backend.
///
/// <para><b>Le défaut n'est pas négociable.</b> <c>Runner:Mode</c> absent, vide, mal orthographié
/// ou inconnu vaut <c>inprocess</c> : une installation existante qui met à jour son binaire ne doit
/// pas se retrouver à composer une adresse qui n'existe pas chez elle. Seule la valeur exacte
/// <c>remote</c> active le tier runner, et elle exige alors une URL et un jeton — sans quoi le
/// démarrage échoue plutôt que de dégrader silencieusement vers le mode en processus, ce qui
/// donnerait un déploiement multi-répliques dont le contrôle des runs serait cassé sans le dire.</para>
/// </summary>
public sealed record RunnerOptions
{
    public RunnerMode Mode { get; init; } = RunnerMode.InProcess;

    /// <summary>Base URL du runner à solliciter pour les nouveaux runs, sans barre oblique finale.</summary>
    public string? BaseUrl { get; init; }

    /// <summary>Jeton porteur partagé, identique à <c>Runner:AuthToken</c> côté runner.</summary>
    public string? AuthToken { get; init; }

    /// <summary>Durée d'une attente longue sur <c>/wait</c>. Bornée côté runner à 120 s.</summary>
    public int WaitTimeoutSeconds { get; init; } = 30;

    /// <summary>Délai d'expiration des appels courts (lancement, arrêt, journaux).</summary>
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Nombre d'échecs consécutifs de l'attente longue avant d'abandonner et de marquer le run en
    /// <c>infra_error</c>. Sans borne, un runner définitivement mort laisserait le run en
    /// <c>running</c> jusqu'à ce que le watchdog s'en aperçoive — c'est-à-dire jusqu'à
    /// <c>maxDurationSeconds</c>, qui peut valoir des heures.
    /// </summary>
    public int MaxConsecutiveWaitFailures { get; init; } = 5;

    public bool IsRemote => Mode == RunnerMode.Remote;

    public static RunnerOptions FromConfiguration(IConfiguration config)
    {
        var raw = (config["Runner:Mode"] ?? string.Empty).Trim().ToLowerInvariant();
        var mode = raw == "remote" ? RunnerMode.Remote : RunnerMode.InProcess;

        var options = new RunnerOptions
        {
            Mode = mode,
            BaseUrl = NullIfBlank(config["Runner:BaseUrl"])?.TrimEnd('/'),
            AuthToken = NullIfBlank(config["Runner:AuthToken"]),
            WaitTimeoutSeconds = PositiveOr(config["Runner:WaitTimeoutSeconds"], 30),
            RequestTimeoutSeconds = PositiveOr(config["Runner:RequestTimeoutSeconds"], 30),
            MaxConsecutiveWaitFailures = PositiveOr(config["Runner:MaxConsecutiveWaitFailures"], 5),
        };

        if (options.IsRemote && string.IsNullOrWhiteSpace(options.BaseUrl))
            throw new InvalidOperationException("Runner:Mode is 'remote' but Runner:BaseUrl is not configured.");

        if (options.IsRemote && string.IsNullOrWhiteSpace(options.AuthToken))
            throw new InvalidOperationException("Runner:Mode is 'remote' but Runner:AuthToken is not configured.");

        return options;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int PositiveOr(string? raw, int fallback) =>
        int.TryParse(raw, out var value) && value > 0 ? value : fallback;
}
