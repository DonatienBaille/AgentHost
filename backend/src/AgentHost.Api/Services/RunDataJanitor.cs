using Dapper;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Infrastructure.Storage;
using AgentHost.Shared.Containers;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Periodic on-disk retention for run data. Without it, every run leaves
/// <c>{Docker:WorkspacePath}/{runId}/workspace</c> (and, if the process died mid-run, a
/// <c>secrets/</c> directory full of plaintext) on the host forever.
///
/// Three independent sweeps, all configurable and all fail-safe (a sweep that throws is logged and
/// retried on the next tick; nothing outside the configured roots is ever touched):
/// <list type="number">
///   <item><b>Orphaned secrets</b> — any <c>{runId}/secrets</c> directory older than
///   <c>Retention:SecretsGraceMinutes</c> (default 60). The orchestrator deletes these as soon as
///   the container exits; this only catches directories orphaned by a crash/restart, so the grace
///   period just has to exceed the time between writing the secrets and starting the container.</item>
///   <item><b>Run workspaces</b> — whole <c>{runId}</c> directories untouched for longer than
///   <c>Retention:WorkspaceHours</c> (default 168 = 7 days). Set to 0 to keep them forever.</item>
///   <item><b>Artifacts</b> — objects older than <c>Retention:ArtifactDays</c>, enumerated through
///   <see cref="IArtifactStorage"/> so the sweep applies to local disk and to an S3 bucket alike.
///   DISABLED by default (0), because artifact rows in the database reference these objects: enable
///   it only with a matching database retention policy, or the API will list artifacts whose bytes
///   are gone. (On S3 a bucket lifecycle rule does the same job server-side and costs no API calls;
///   this sweep exists so the behaviour does not silently depend on which backend is configured.)</item>
/// </list>
///
/// Note that the workspace sweep is deliberately driven by the backend's OWN view of the run
/// directory (<c>Docker:WorkspacePath</c>), not the daemon's (<c>Docker:HostWorkspacePath</c>): this
/// process is the one doing the deleting.
/// </summary>
public class RunDataJanitor : BackgroundService
{
    private readonly ILogger _logger;
    private readonly IArtifactStorage _artifactStorage;
    private readonly string _workspaceRoot;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _secretsGrace;
    private readonly TimeSpan _workspaceRetention;
    private readonly TimeSpan _artifactRetention;
    private readonly TimeSpan _triggerDeliveryRetention;
    private readonly IDbConnectionFactory _connectionFactory;

    public RunDataJanitor(
        IConfiguration config,
        ILogger logger,
        IArtifactStorage artifactStorage,
        ContainerPathMapper paths,
        IDbConnectionFactory connectionFactory)
    {
        _logger = logger;
        _artifactStorage = artifactStorage;
        _workspaceRoot = paths.LocalWorkspaceRoot;
        _connectionFactory = connectionFactory;

        _interval = TimeSpan.FromMinutes(ReadPositiveDouble(config, "Retention:SweepIntervalMinutes", 60));
        _secretsGrace = TimeSpan.FromMinutes(ReadPositiveDouble(config, "Retention:SecretsGraceMinutes", 60));
        _workspaceRetention = TimeSpan.FromHours(ReadPositiveDouble(config, "Retention:WorkspaceHours", 168));
        _artifactRetention = TimeSpan.FromDays(ReadPositiveDouble(config, "Retention:ArtifactDays", 0));

        // Les livraisons de webhook ne servent qu'à la déduplication, dont la fenêtre utile est
        // celle des réémissions d'une forge — quelques heures chez GitHub. Trente jours laissent
        // une marge confortable ; au-delà, ce sont des lignes que plus rien ne consultera jamais.
        _triggerDeliveryRetention = TimeSpan.FromDays(
            ReadPositiveDouble(config, "Retention:TriggerDeliveryDays", 30));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short initial delay keeps startup (and test host boot) free of disk work.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Run data retention sweep failed");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        SweepRunDirectories();
        await SweepArtifactsAsync(ct);
        await SweepTriggerDeliveriesAsync(ct);
    }

    /// <summary>
    /// Purge les livraisons de webhook trop anciennes pour servir encore à la déduplication
    /// (feuille de route, lot 4).
    ///
    /// <b>Pourquoi ces lignes doivent partir.</b> `trigger_deliveries` ne fait que grossir : une
    /// ligne par livraison reçue, pour toujours. Sur un dépôt actif, c'est la seule table du schéma
    /// dont la croissance n'est bornée par rien — ni par un nombre de runs, ni par un nombre
    /// d'utilisateurs, seulement par le trafic entrant.
    ///
    /// <b>Pourquoi c'est sûr.</b> Ces lignes n'existent que pour reconnaître une réémission. Une
    /// forge réémet dans les minutes ou les heures qui suivent, jamais dans les semaines : une
    /// entrée d'il y a trente jours ne protège plus de rien. La seule conséquence d'une purge trop
    /// agressive serait un run relancé pour une livraison ancienne rejouée à la main — d'où une
    /// fenêtre large et non serrée.
    ///
    /// Une erreur ici n'interrompt pas la passe : le ménage est une commodité, pas une garantie.
    /// </summary>
    private async Task SweepTriggerDeliveriesAsync(CancellationToken ct)
    {
        if (_triggerDeliveryRetention <= TimeSpan.Zero) return;

        try
        {
            using var db = _connectionFactory.CreateConnection();
            var deleted = await db.ExecuteAsync(new CommandDefinition(
                "DELETE FROM trigger_deliveries WHERE received_at < @Cutoff",
                new { Cutoff = DateTime.UtcNow - _triggerDeliveryRetention },
                cancellationToken: ct));

            if (deleted > 0)
                _logger.Information("Purged {Count} trigger deliveries older than {Retention}",
                    deleted, _triggerDeliveryRetention);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Could not purge trigger deliveries");
        }
    }

    private void SweepRunDirectories()
    {
        if (!Directory.Exists(_workspaceRoot))
            return;

        var now = DateTime.UtcNow;

        foreach (var runDir in Directory.EnumerateDirectories(_workspaceRoot))
        {
            try
            {
                var secretsDir = Path.Combine(runDir, "secrets");
                if (Directory.Exists(secretsDir) &&
                    now - Directory.GetLastWriteTimeUtc(secretsDir) > _secretsGrace)
                {
                    Directory.Delete(secretsDir, recursive: true);
                    _logger.Warning("Deleted orphaned plaintext secrets directory {Directory} " +
                                    "(no container exit cleaned it up; likely a crash or restart)", secretsDir);
                }

                if (_workspaceRetention > TimeSpan.Zero &&
                    now - LastActivityUtc(runDir) > _workspaceRetention)
                {
                    Directory.Delete(runDir, recursive: true);
                    _logger.Information("Deleted expired run directory {Directory} (retention {Retention})",
                        runDir, _workspaceRetention);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not apply retention to run directory {Directory}", runDir);
            }
        }
    }

    private async Task SweepArtifactsAsync(CancellationToken ct)
    {
        if (_artifactRetention <= TimeSpan.Zero)
            return;

        var cutoff = DateTime.UtcNow - _artifactRetention;

        await foreach (var stored in _artifactStorage.ListAsync(ct))
        {
            if (stored.LastModifiedUtc >= cutoff)
                continue;

            try
            {
                await _artifactStorage.DeleteAsync(stored.Key, ct);
                _logger.Information("Deleted expired artifact {Key} from {Provider} storage (retention {Retention})",
                    stored.Key, _artifactStorage.ProviderName, _artifactRetention);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not apply retention to artifact {Key}", stored.Key);
            }
        }
    }

    /// <summary>
    /// Newest mtime anywhere in the run directory. The directory's own mtime only tracks its direct
    /// entries, so a long run writing deep inside workspace/ would otherwise look idle.
    /// </summary>
    private static DateTime LastActivityUtc(string runDir)
    {
        var newest = Directory.GetLastWriteTimeUtc(runDir);

        foreach (var entry in Directory.EnumerateFileSystemEntries(runDir, "*", SearchOption.AllDirectories))
        {
            var written = File.GetLastWriteTimeUtc(entry);
            if (written > newest)
                newest = written;
        }

        return newest;
    }

    private static double ReadPositiveDouble(IConfiguration config, string key, double fallback)
        => double.TryParse(config[key], out var value) && value >= 0 ? value : fallback;
}
