using System.Globalization;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Une expression cron à cinq champs, et le calcul de sa prochaine échéance (feuille de route,
/// lot 4).
///
/// <b>Pourquoi c'est écrit ici plutôt qu'ajouté en dépendance.</b> Le besoin est un sous-ensemble
/// bien délimité — cinq champs, la syntaxe POSIX, pas de secondes, pas de <c>@reboot</c>, pas de
/// <c>L</c> ni de <c>#</c> — et il se teste exhaustivement. Les bibliothèques du domaine apportent
/// en prime un moteur de planification, un modèle de tâches et leurs propres décisions sur les
/// fuseaux, dont aucune n'est celle qu'on veut ici. Le même arbitrage a été fait pour le TOTP.
///
/// <b>Le fuseau n'est pas un détail.</b> « Tous les jours à 9 h » n'a pas de sens sans lui, et
/// résoudre l'expression en UTC déplacerait l'exécution de deux heures deux fois par an pour toute
/// équipe qui n'est pas à Greenwich. L'expression est donc évaluée dans le fuseau du déclencheur,
/// et seule la conversion finale ramène en UTC — ce que la base stocke.
///
/// <b>Jour du mois et jour de la semaine se combinent en OU</b>, et non en ET, quand les deux sont
/// restreints. C'est la règle POSIX, contre-intuitive mais universelle : <c>0 0 1 * 1</c> signifie
/// « le premier du mois, ET AUSSI tous les lundis ». Faire autrement produirait des planifications
/// qui divergent silencieusement de ce que la même expression donne dans n'importe quel crontab.
/// </summary>
public sealed class CronSchedule
{
    private readonly bool[] _minutes = new bool[60];
    private readonly bool[] _hours = new bool[24];
    private readonly bool[] _daysOfMonth = new bool[32]; // 1..31
    private readonly bool[] _months = new bool[13];      // 1..12
    private readonly bool[] _daysOfWeek = new bool[7];   // 0 = dimanche

    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    /// <summary>L'expression telle qu'elle a été fournie, pour l'afficher et la persister.</summary>
    public string Expression { get; }

    /// <summary>
    /// Au-delà de cette profondeur, on considère qu'une expression ne se déclenchera jamais.
    /// Quatre ans couvrent le 29 février, seul cas légitime d'attente longue.
    /// </summary>
    private const int MaxSearchDays = 4 * 366;

    private static readonly string[] MonthNames =
        ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    private static readonly string[] DayNames = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    private CronSchedule(string expression, string[] fields)
    {
        Expression = expression;

        ParseField(fields[0], 0, 59, _minutes, null);
        ParseField(fields[1], 0, 23, _hours, null);
        ParseField(fields[2], 1, 31, _daysOfMonth, null);
        ParseField(fields[3], 1, 12, _months, MonthNames);
        ParseField(fields[4], 0, 6, _daysOfWeek, DayNames);

        _dayOfMonthRestricted = !IsWildcard(fields[2]);
        _dayOfWeekRestricted = !IsWildcard(fields[4]);
    }

    /// <summary>Analyse une expression, ou lève <see cref="FormatException"/> en expliquant pourquoi.</summary>
    public static CronSchedule Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new FormatException("A cron expression is required.");

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException(
                $"A cron expression has 5 fields (minute hour day-of-month month day-of-week); got {fields.Length}.");

        return new CronSchedule(expression.Trim(), fields);
    }

    public static bool TryParse(string expression, out CronSchedule? schedule, out string? error)
    {
        try
        {
            schedule = Parse(expression);
            error = null;
            return true;
        }
        catch (FormatException ex)
        {
            schedule = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// La prochaine échéance <b>strictement postérieure</b> à <paramref name="afterUtc"/>, en UTC ;
    /// null si l'expression ne se déclenche pas dans les quatre prochaines années (par exemple
    /// <c>0 0 30 2 *</c>, le 30 février).
    ///
    /// Strictement postérieure, et jamais égale : renvoyer l'instant courant ferait boucler le
    /// planificateur sur la même échéance indéfiniment.
    /// </summary>
    public DateTime? GetNextOccurrence(DateTime afterUtc, TimeZoneInfo timeZone)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), timeZone);

        // On repart de la minute suivante, secondes tronquées : cron a la minute pour grain.
        var start = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0)
            .AddMinutes(1);

        for (var dayOffset = 0; dayOffset < MaxSearchDays; dayOffset++)
        {
            var date = start.Date.AddDays(dayOffset);
            if (!MatchesDate(date)) continue;

            // Le premier jour est le seul partiellement écoulé ; les suivants s'explorent en entier.
            var firstHour = dayOffset == 0 ? start.Hour : 0;

            for (var hour = firstHour; hour < 24; hour++)
            {
                if (!_hours[hour]) continue;

                var firstMinute = dayOffset == 0 && hour == start.Hour ? start.Minute : 0;
                for (var minute = firstMinute; minute < 60; minute++)
                {
                    if (!_minutes[minute]) continue;

                    var candidate = date.AddHours(hour).AddMinutes(minute);
                    var utc = ToUtc(candidate, timeZone);
                    // Heure inexistante (passage à l'heure d'été) : l'échéance de ce jour-là n'a
                    // pas lieu, on continue plutôt que d'inventer une exécution.
                    if (utc is not null) return utc;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Convertit une heure locale en UTC, ou null si cette heure n'existe pas dans ce fuseau.
    ///
    /// Une heure ambiguë (le passage à l'heure d'hiver la répète) est résolue par
    /// <see cref="TimeZoneInfo"/> vers l'occurrence en heure standard, c'est-à-dire la seconde :
    /// l'exécution a lieu une fois, pas deux, et c'est ce qu'on attend d'une planification.
    /// </summary>
    private static DateTime? ToUtc(DateTime local, TimeZoneInfo timeZone)
    {
        if (timeZone.IsInvalidTime(local)) return null;
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone);
    }

    private bool MatchesDate(DateTime date)
    {
        if (!_months[date.Month]) return false;

        var dayOfMonthMatches = _daysOfMonth[date.Day];
        var dayOfWeekMatches = _daysOfWeek[(int)date.DayOfWeek];

        // La règle POSIX : quand les deux champs sont restreints, ils s'additionnent au lieu de se
        // croiser. Quand un seul l'est, l'autre vaut « tous » et le ET habituel suffit.
        if (_dayOfMonthRestricted && _dayOfWeekRestricted)
            return dayOfMonthMatches || dayOfWeekMatches;

        return dayOfMonthMatches && dayOfWeekMatches;
    }

    private static bool IsWildcard(string field) => field == "*" || field.StartsWith("*/", StringComparison.Ordinal);

    private static void ParseField(string field, int min, int max, bool[] target, string[]? names)
    {
        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = 1;
            var range = part;

            var slash = part.IndexOf('/');
            if (slash >= 0)
            {
                range = part[..slash];
                var stepText = part[(slash + 1)..];
                if (!int.TryParse(stepText, NumberStyles.None, CultureInfo.InvariantCulture, out step) || step < 1)
                    throw new FormatException($"Invalid step '{stepText}' in cron field '{field}'.");
            }

            int from, to;
            if (range == "*")
            {
                (from, to) = (min, max);
            }
            else
            {
                var dash = range.IndexOf('-', 1);
                if (dash > 0)
                {
                    from = ParseValue(range[..dash], min, max, names, field);
                    to = ParseValue(range[(dash + 1)..], min, max, names, field);
                }
                else
                {
                    from = ParseValue(range, min, max, names, field);
                    // Sans borne haute, un pas s'applique jusqu'au maximum du champ : `5/10` en
                    // minutes vaut 5, 15, 25… C'est le comportement de Vixie cron.
                    to = slash >= 0 ? max : from;
                }
            }

            if (from > to)
                throw new FormatException($"Inverted range '{range}' in cron field '{field}'.");

            for (var value = from; value <= to; value += step)
                target[Normalize(value, max)] = true;
        }

        if (Array.TrueForAll(target, set => !set))
            throw new FormatException($"Cron field '{field}' matches nothing.");
    }

    /// <summary>Dimanche s'écrit 0 ou 7 ; les deux désignent le même jour.</summary>
    private static int Normalize(int value, int max) => max == 6 && value == 7 ? 0 : value;

    private static int ParseValue(string token, int min, int max, string[]? names, string field)
    {
        if (int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            // 7 = dimanche est accepté en entrée, et ramené sur 0 par Normalize.
            var upper = max == 6 ? 7 : max;
            if (number < min || number > upper)
                throw new FormatException($"Value '{token}' is out of range [{min}, {max}] in cron field '{field}'.");
            return number;
        }

        if (names is not null)
        {
            var index = Array.IndexOf(names, token.ToLowerInvariant());
            if (index >= 0) return index + min;
        }

        throw new FormatException($"Unrecognized value '{token}' in cron field '{field}'.");
    }
}
