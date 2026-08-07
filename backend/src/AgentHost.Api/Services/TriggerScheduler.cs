using System.Text.Json.Nodes;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Le planificateur des déclencheurs cron (feuille de route, lot 4).
///
/// <b>Il bat, il ne dort pas jusqu'à l'échéance.</b> Attendre précisément la prochaine occurrence
/// serait plus élégant et faux : les déclencheurs sont créés, modifiés et supprimés pendant
/// l'attente, et un processus endormi pour six heures ne verrait rien de tout cela. Un battement
/// court interroge un index sur une colonne date — le coût est celui d'un parcours d'index vide,
/// des dizaines de fois par minute, ce qui ne se mesure pas.
///
/// <b>Plusieurs répliques sont prévues, pas tolérées.</b> C'est tout l'intérêt du tier runner du
/// lot 1 : le backend tourne à plus d'un exemplaire. Deux instances voient donc la même échéance
/// au même battement. La réservation se fait en une seule instruction — <c>UPDATE … WHERE
/// next_run_at = @Expected RETURNING</c> — de sorte que la seconde ne réserve rien et n'a rien à
/// faire. Aucun verrou distribué, aucune élection de chef : la ligne elle-même est le jeton.
///
/// <b>Les occurrences manquées ne sont pas rattrapées.</b> Un backend arrêté trois jours ne doit
/// pas, au redémarrage, lancer soixante-douze fois l'agent horaire d'un utilisateur — ce serait la
/// facture d'une panne payée deux fois. La prochaine échéance est recalculée à partir de
/// maintenant, et l'occurrence manquée est journalisée.
/// </summary>
public class TriggerScheduler : BackgroundService
{
    /// <summary>Période du battement. Cron a la minute pour grain ; battre plus vite ne sert à rien.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(20);

    /// <summary>Combien d'échéances au plus par battement, pour qu'un retard ne bloque pas la boucle.</summary>
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;

    public TriggerScheduler(IServiceScopeFactory scopeFactory, ILogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("Trigger scheduler started (beat {Seconds}s)", Beat.TotalSeconds);

        using var timer = new PeriodicTimer(Beat);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // La boucle survit à tout : une base momentanément injoignable ne doit pas
                // éteindre définitivement la planification de l'installation.
                _logger.Error(ex, "Trigger scheduler beat failed");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.Information("Trigger scheduler stopped");
    }

    /// <summary>Un battement : réserver ce qui est échu, et le lancer. Exposé pour les tests.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITriggerRepository>();
        var triggers = scope.ServiceProvider.GetRequiredService<ITriggerService>();

        var now = DateTime.UtcNow;
        var due = await repository.ListDueUnscopedAsync(now, BatchSize, ct);
        var fired = 0;

        foreach (var trigger in due)
        {
            if (ct.IsCancellationRequested) break;
            if (trigger.NextRunAt is null || trigger.CronExpression is null) continue;

            var next = ComputeNextRun(trigger, now);
            if (next is null)
            {
                // Une expression qui ne se déclenchera plus jamais (le 30 février) : `next_run_at
                // = NULL` la sort de l'index des échéances, alors qu'une date lointaine la ferait
                // reparcourir à chaque battement pour toujours.
                _logger.Warning("Trigger {TriggerId} has no future occurrence; unscheduling", trigger.Id);
                await repository.ClaimDueAsync(trigger.Id, trigger.NextRunAt.Value, null, ct);
                continue;
            }

            // La réservation et le calcul de la suivante en une seule écriture. Perdre la course
            // ici est le cas normal à plusieurs répliques, pas une anomalie.
            var claimed = await repository.ClaimDueAsync(trigger.Id, trigger.NextRunAt.Value, next.Value, ct);
            if (claimed is null) continue;

            var overdue = now - trigger.NextRunAt.Value;
            if (overdue > TimeSpan.FromMinutes(5))
                _logger.Warning("Trigger {TriggerId} fired {Late} late; missed occurrences are not replayed",
                    trigger.Id, overdue);

            var context = new JsonObject
            {
                ["trigger"] = new JsonObject
                {
                    ["id"] = trigger.Id,
                    ["name"] = trigger.Name,
                    ["type"] = trigger.Type.ToDbString(),
                    ["cron"] = trigger.CronExpression,
                    ["timeZone"] = trigger.TimeZone,
                    ["scheduledFor"] = trigger.NextRunAt.Value,
                },
            };

            var run = await triggers.FireAsync(claimed, TriggeredByType.Cron, context, ct);
            if (run is not null) fired++;
        }

        return fired;
    }

    /// <summary>
    /// La prochaine occurrence après maintenant.
    ///
    /// À partir de <b>maintenant</b> et non de l'échéance manquée : c'est ce qui empêche un
    /// redémarrage après panne de rejouer d'un coup toutes les occurrences de l'interruption.
    /// </summary>
    private DateTime? ComputeNextRun(Trigger trigger, DateTime now)
    {
        try
        {
            var schedule = CronSchedule.Parse(trigger.CronExpression!);
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(trigger.TimeZone);
            return schedule.GetNextOccurrence(now, timeZone);
        }
        catch (Exception ex) when (ex is FormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Les deux sont validés à l'écriture ; y arriver signifie qu'une ligne a été modifiée
            // hors de l'application, ou qu'un fuseau a disparu de la base tzdata de l'hôte.
            _logger.Error(ex, "Trigger {TriggerId} carries an unusable schedule", trigger.Id);
            return null;
        }
    }
}
