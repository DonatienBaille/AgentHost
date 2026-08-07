using AgentHost.Shared.Contracts;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace AgentHost.Shared.Containers;

/// <summary>
/// La plomberie conteneur, et seulement elle : créer, démarrer, attendre, arrêter, lire les
/// journaux, supprimer, effacer les secrets. Aucune base de données, aucune machine à états, aucun
/// bus d'événements — c'est ce qui permet de faire tourner exactement ce code à deux endroits :
/// dans le backend (mode <c>inprocess</c>, via <c>ContainerOrchestrator</c>) et dans le tier runner
/// (mode <c>remote</c>, via l'API HTTP d'AgentHost.Runner).
///
/// <para>Extrait de <c>ContainerOrchestrator</c> sans changement de comportement : les mêmes
/// atténuations de la section 13 de la spécification sont appliquées sur le HostConfig
/// (no-new-privileges, abandon complet des capacités + NET_BIND_SERVICE seulement si le manifeste
/// autorise le réseau, mémoire stricte sans swap, rootfs en lecture seule avec /workspace inscriptible
/// et /tmp en tmpfs, montage des secrets en lecture seule détruit dès la sortie du conteneur, DNS
/// configurable, délai d'arrêt dérivé de la durée maximale du run).</para>
///
/// <para>Les secrets ne sont livrés QUE sous forme de fichiers dans <c>/run/secrets/&lt;NOM&gt;</c>.
/// Ils ne sont délibérément pas exportés en variables <c>SECRET_*</c> : l'environnement d'un
/// conteneur est lisible par quiconque peut appeler <c>docker inspect</c> et par tout processus du
/// conteneur via <c>/proc/1/environ</c>, ce qui annulerait le montage en lecture seule. Voir
/// <c>docs/agent-protocol.md</c> section 1.</para>
///
/// <para>Politique réseau (<c>spec.permissions.network</c>) : déléguée à
/// <see cref="NetworkPolicyResolver"/>. LIMITATION RÉSIDUELLE inchangée : un filtrage par proxy
/// n'engage que les clients qui respectent <c>HTTP_PROXY</c> ; l'application réelle demande que le
/// réseau du conteneur ne route que vers le proxy (<c>Docker:AllowlistNetwork</c> sur un réseau
/// <c>--internal</c>).</para>
/// </summary>
public sealed class ContainerLauncher
{
    private readonly DockerClient _docker;
    private readonly ContainerPathMapper _paths;
    private readonly ContainerLauncherOptions _options;
    private readonly ILogger _logger;

    public ContainerLauncher(
        DockerClient docker,
        ContainerPathMapper paths,
        ContainerLauncherOptions options,
        ILogger logger)
    {
        _docker = docker;
        _paths = paths;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Convertit <c>runtime.cpu</c> (en cœurs) vers l'unité NanoCPUs du démon. Une valeur nulle ou
    /// négative signifie « illimité », ce qu'exprime un champ laissé à 0.
    /// </summary>
    internal static long NanoCpus(long cpuCores) => cpuCores > 0 ? cpuCores * 1_000_000_000L : 0L;

    /// <summary>
    /// Écrit les secrets, tire l'image, crée et démarre le conteneur. Renvoie son identifiant.
    /// En cas d'échec, les secrets déjà écrits sont effacés avant que l'exception ne remonte :
    /// aucun conteneur ne viendra le faire.
    /// </summary>
    public async Task<string> CreateAndStartAsync(AgentLaunchSpec spec, CancellationToken ct)
    {
        try
        {
            var workspaceDir = _paths.LocalWorkspaceDirectory(spec.RunId);
            var secretsDir = _paths.LocalSecretsDirectory(spec.RunId);

            Directory.CreateDirectory(workspaceDir);
            Directory.CreateDirectory(secretsDir);
            // 0700 sur le répertoire, 0400 sur chaque fichier : c'est le mode Unix, et non un
            // attribut « lecture seule », qui tient les autres comptes locaux à l'écart du clair.
            // (Dans le conteneur le montage est cloisonné ; un conteneur tournant en uid 0 lit
            // toujours le fichier, ce qui est exactement le chemin de livraison voulu.)
            SetUnixMode(secretsDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            foreach (var (key, value) in spec.Secrets)
            {
                var secretPath = Path.Combine(secretsDir, key);
                await File.WriteAllTextAsync(secretPath, value, ct);
                SetUnixMode(secretPath, UnixFileMode.UserRead);
            }

            _logger.Information("Pulling image {ImageRef} for run {RunId}", spec.ImageRef, spec.RunId);

            try
            {
                var progress = new Progress<JSONMessage>(msg =>
                {
                    if (!string.IsNullOrEmpty(msg.Status))
                        _logger.Debug("Docker: {Status}", msg.Status);
                });

                await _docker.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = spec.ImageRef },
                    null,
                    progress,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to pull image {ImageRef}, using cached", spec.ImageRef);
            }

            var network = NetworkPolicyResolver.Resolve(
                spec.Network, spec.NetworkAllowlist, spec.RunId, _options.NetworkSettings, _logger);

            var envVars = new List<string>(spec.Env);
            envVars.AddRange(network.EnvVars);

            var capAdd = network.HasNetwork ? new[] { "NET_BIND_SERVICE" } : Array.Empty<string>();

            var labels = new Dictionary<string, string>
            {
                { "agenthost.run_id", spec.RunId },
                { "agenthost.project_id", spec.ProjectId },
                { "agenthost.agent_id", spec.AgentId },
                { "agenthost.network", network.Mode },
            };
            if (network.Allowlist.Count > 0)
                labels["agenthost.network_allowlist"] = string.Join(",", network.Allowlist);

            if (spec.WritableRootfs)
            {
                _logger.Warning(
                    "Run {RunId} uses agent {AgentId} whose manifest opts into a writable root filesystem " +
                    "(spec.permissions.writableRootfs: true); the container is less isolated than the default",
                    spec.RunId, spec.AgentId);
            }

            var containerResponse = await _docker.Containers.CreateContainerAsync(
                new CreateContainerParameters
                {
                    Image = spec.ImageRef,
                    Name = $"agenthost-run-{spec.RunId}",
                    Hostname = $"run-{spec.RunNumber}",
                    Env = envVars,

                    // maxDuration + 30 s de grâce avant que Docker ne tue le conteneur
                    // (spec 13.2). S'applique à `docker stop`, pas à la machine à états du run.
                    StopTimeout = TimeSpan.FromSeconds(spec.MaxDurationSeconds + 30),

                    HostConfig = new HostConfig
                    {
                        // Ressources.
                        //
                        // NanoCPUs et non CPUCount : CPUCount est un champ « conteneur Windows »
                        // que le démon Docker Linux comme Podman ignorent purement et simplement,
                        // si bien que runtime.cpu ne contraignait rien. NanoCPUs est le mécanisme
                        // Linux portable (cœurs x 1e9 ; le démon en fait cpu.max / CpuQuota+
                        // CpuPeriod) et l'API compat de Podman le traduit pareil.
                        NanoCPUs = NanoCpus(spec.CpuCores),
                        Memory = spec.MemoryBytes,
                        MemorySwap = spec.MemoryBytes, // pas de swap

                        // Sécurité (spec section 13.2)
                        SecurityOpt = _options.SecurityOpt,

                        // Runtime isolé (gVisor « runsc », Kata « kata-runtime », « sysbox-runc »).
                        // Vide = le défaut du démon (runc), qui partage le noyau de l'hôte : une
                        // évasion de conteneur est une compromission de l'hôte. Pour une plateforme
                        // dont le métier est d'exécuter du code tiers c'est l'atténuation
                        // structurelle, et elle coûte un champ — mais le runtime doit d'abord être
                        // installé et déclaré auprès du démon, donc ce ne peut pas être le défaut.
                        Runtime = _options.ContainerRuntime,
                        // « ALL » en majuscules : les deux moteurs normalisent les noms de
                        // capacités, mais le chemin compat de Podman compare la sentinelle
                        // « tout abandonner » en tenant compte de la casse par endroits, et « ALL »
                        // est l'orthographe que les deux acceptent.
                        CapDrop = new[] { "ALL" },
                        CapAdd = capAdd,

                        // Rootfs en lecture seule par défaut : l'agent écrit dans le montage
                        // /workspace et dans le tmpfs /tmp, nulle part ailleurs. Un manifeste qui a
                        // réellement besoin d'un rootfs inscriptible doit le demander explicitement.
                        ReadonlyRootfs = !spec.WritableRootfs,

                        // Volumes. Les sources sont la vue du DÉMON sur ces répertoires, pas celle
                        // de ce processus — voir ContainerPathMapper.
                        Binds = new[]
                        {
                            _paths.BindSpec(workspaceDir, "/workspace"),
                            _paths.BindSpec(secretsDir, "/run/secrets", "ro"),
                        },
                        Tmpfs = new Dictionary<string, string>
                        {
                            { "/tmp", $"rw,nosuid,nodev,size={_options.TmpfsSizeMb}m" },
                        },

                        // Réseau
                        NetworkMode = network.Mode,
                        // Omis (vide) tant que Docker:Dns n'est pas configuré, pour que les
                        // conteneurs héritent du résolveur du démon plutôt que d'un résolveur
                        // public codé en dur.
                        DNS = _options.Dns,
                        DNSSearch = Array.Empty<string>(),
                    },

                    Labels = labels,
                },
                ct);

            await _docker.Containers.StartContainerAsync(
                containerResponse.ID,
                new ContainerStartParameters(),
                ct);

            _logger.Information("Container {ContainerId} started for run {RunId}",
                containerResponse.ID, spec.RunId);

            return containerResponse.ID;
        }
        catch (Exception)
        {
            // Le conteneur n'a jamais démarré (ou pas assez loin pour être surveillé) : rien
            // d'autre ne viendra nettoyer les secrets en clair déjà écrits.
            DeleteRunSecrets(spec.RunId);
            throw;
        }
    }

    /// <summary>Attend la sortie du conteneur et renvoie son code de sortie.</summary>
    public async Task<long> WaitAsync(string containerId, CancellationToken ct)
    {
        var response = await _docker.Containers.WaitContainerAsync(containerId, ct);
        return response.StatusCode;
    }

    /// <summary>
    /// Journaux d'un conteneur désigné par son identifiant. Les conteneurs tournent sans TTY, donc
    /// stdout/stderr reviennent multiplexés (entrelacés avec des en-têtes de trame de 8 octets) ;
    /// la surcharge <c>tty: false</c> les démultiplexe en un couple (stdout, stderr) propre.
    /// </summary>
    public async Task<string> CollectLogsAsync(string containerId, CancellationToken ct)
    {
        using var logs = await _docker.Containers.GetContainerLogsAsync(
            containerId,
            false,
            new ContainerLogsParameters { ShowStdout = true, ShowStderr = true, Follow = false },
            ct);

        var (stdout, stderr) = await logs.ReadOutputToEndAsync(ct);
        return stdout + stderr;
    }

    public Task RemoveAsync(string containerId, CancellationToken ct) =>
        _docker.Containers.RemoveContainerAsync(containerId, new ContainerRemoveParameters { Force = true }, ct);

    /// <summary>
    /// Arrête tout conteneur portant l'étiquette <c>agenthost.run_id</c> du run.
    /// <see cref="RunnerStopResponse.Confirmed"/> distingue « plus rien ne tourne ici » (vrai, y
    /// compris quand il n'y avait aucun conteneur) de « le démon a refusé » (faux) — parce qu'un
    /// arrêt raté annoncé comme un succès est le pire résultat possible pour qui annule un run.
    /// </summary>
    public async Task<RunnerStopResponse> StopAsync(string runId, CancellationToken ct)
    {
        try
        {
            var containers = await ListByRunAsync(runId, all: false, ct);

            var stopped = 0;
            foreach (var container in containers)
            {
                await _docker.Containers.StopContainerAsync(
                    container.ID,
                    new ContainerStopParameters { WaitBeforeKillSeconds = 10 },
                    ct);

                stopped++;
                _logger.Information("Container {ContainerId} stopped for run {RunId}", container.ID, runId);
            }

            return new RunnerStopResponse { Stopped = stopped, Confirmed = true };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error stopping container for run {RunId}", runId);
            return new RunnerStopResponse { Stopped = 0, Confirmed = false, Detail = ex.Message };
        }
    }

    /// <summary>Journaux du conteneur d'un run, désigné par son étiquette.</summary>
    public async Task<RunnerLogsResponse> GetLogsAsync(string runId, CancellationToken ct)
    {
        try
        {
            var containers = await ListByRunAsync(runId, all: true, ct);
            if (containers.Count == 0)
            {
                return new RunnerLogsResponse
                {
                    Logs = string.Empty,
                    Retrieved = false,
                    Detail = $"No container found for run {runId}",
                };
            }

            var logs = await CollectLogsAsync(containers[0].ID, ct);
            return new RunnerLogsResponse { Logs = logs, Retrieved = true };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error getting logs for run {RunId}", runId);
            return new RunnerLogsResponse { Logs = string.Empty, Retrieved = false, Detail = ex.Message };
        }
    }

    private async Task<IList<ContainerListResponse>> ListByRunAsync(string runId, bool all, CancellationToken ct) =>
        (IList<ContainerListResponse>)await _docker.Containers.ListContainersAsync(
            new ContainersListParameters
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    { "label", new Dictionary<string, bool> { { $"agenthost.run_id={runId}", true } } },
                },
                All = all,
            },
            ct);

    /// <summary>
    /// Retire du disque les secrets en clair du run. Appelé dès la sortie du conteneur (et sur un
    /// lancement échoué) : ils ne servent que le temps du montage, et au-delà ce n'est que du
    /// matériel de clé non chiffré qui traîne dans le répertoire des runs. Idempotent.
    /// </summary>
    public void DeleteRunSecrets(string runId)
    {
        var dir = _paths.LocalSecretsDirectory(runId);
        try
        {
            if (!Directory.Exists(dir))
                return;

            Directory.Delete(dir, recursive: true);
            _logger.Debug("Deleted secrets directory for run {RunId}", runId);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to delete secrets directory {Directory} for run {RunId}; " +
                              "plaintext secrets may remain on disk", dir, runId);
        }
    }

    private static void SetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return; // File.SetUnixFileMode lève PlatformNotSupportedException sur Windows.

        File.SetUnixFileMode(path, mode);
    }
}

/// <summary>
/// Réglages nœud-locaux du lancement de conteneurs (section <c>Docker:</c>). Identiques dans le
/// backend et dans le runner : c'est le même code qui les lit, donc un opérateur n'a pas deux
/// grammaires à connaître selon le mode.
/// </summary>
public sealed record ContainerLauncherOptions
{
    /// <summary>
    /// Vide par défaut : les conteneurs héritent de la configuration de résolution du démon. Un
    /// déploiement souverain règle <c>Docker:Dns:0/1/...</c> sur ses résolveurs internes ; aucun
    /// résolveur public tiers n'est jamais utilisé implicitement.
    /// </summary>
    public string[] Dns { get; init; } = Array.Empty<string>();

    public string? ContainerRuntime { get; init; }
    public int TmpfsSizeMb { get; init; } = 64;
    public string[] SecurityOpt { get; init; } = { "no-new-privileges=true" };
    public NetworkPolicyResolver.Settings NetworkSettings { get; init; } =
        new(null, null, "localhost,127.0.0.1,::1", false);

    public static ContainerLauncherOptions FromConfiguration(IConfiguration config) => new()
    {
        Dns = config.GetSection("Docker:Dns").Get<string[]>() ?? Array.Empty<string>(),
        ContainerRuntime = NullIfBlank(config["Docker:Runtime"]),
        TmpfsSizeMb = int.TryParse(config["Docker:TmpfsSizeMb"], out var mb) && mb > 0 ? mb : 64,
        SecurityOpt = ResolveSecurityOpt(config),
        NetworkSettings = new NetworkPolicyResolver.Settings(
            config["Docker:EgressProxy"],
            NullIfBlank(config["Docker:AllowlistNetwork"]),
            config["Docker:NoProxy"] ?? "localhost,127.0.0.1,::1",
            bool.TryParse(config["Docker:AllowUnconfinedAllowlist"], out var unconfined) && unconfined),
    };

    /// <summary>
    /// Options de sécurité du conteneur. Configurable (<c>Docker:SecurityOpt:0/1/...</c>) uniquement
    /// parce que l'orthographe acceptée de <c>no-new-privileges</c> a historiquement varié selon les
    /// moteurs et les versions : le démon Docker accepte la clé nue, <c>=true</c> et <c>:true</c> ;
    /// le gestionnaire compat de Podman découpe d'abord sur <c>=</c> et traite aussi la clé nue en
    /// cas particulier. <c>=true</c> est la forme que les deux analysent aujourd'hui et reste le
    /// défaut ; un exploitant qui se heurte à un « invalid --security-opt » sur un Podman ancien peut
    /// poser la clé nue sans changer de code.
    /// </summary>
    private static string[] ResolveSecurityOpt(IConfiguration config)
    {
        var configured = config.GetSection("Docker:SecurityOpt").Get<string[]>();
        return configured is { Length: > 0 } ? configured : new[] { "no-new-privileges=true" };
    }

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
