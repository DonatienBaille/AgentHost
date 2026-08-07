using AgentHost.Api.Infrastructure;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// L'analyse et l'évaluation des expressions cron (lot 4).
///
/// <b>Pourquoi ces tests sont denses.</b> Une planification fausse ne se voit pas : l'agent part
/// une heure trop tard, ou pas du tout, et personne ne le remarque avant la facture ou la réunion
/// où le rapport manque. Il n'y a pas de « à peu près » possible ici, et c'est justement le genre
/// de logique qu'on écrit une fois et qu'on ne relit jamais — d'où une couverture exhaustive des
/// cas où l'intuition se trompe : la règle OU entre jour du mois et jour de la semaine, les
/// passages à l'heure d'été, et le 29 février.
/// </summary>
public class CronScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly TimeZoneInfo Paris = TimeZoneInfo.FindSystemTimeZoneById("Europe/Paris");

    private static DateTime Next(string expression, DateTime afterUtc, TimeZoneInfo? zone = null) =>
        CronSchedule.Parse(expression).GetNextOccurrence(afterUtc, zone ?? Utc)
            ?? throw new InvalidOperationException($"'{expression}' produced no occurrence");

    [Fact]
    public void Every_minute_advances_by_one_minute()
    {
        var now = new DateTime(2031, 3, 3, 12, 30, 45, DateTimeKind.Utc);

        // Strictement postérieure, et les secondes tombent : cron a la minute pour grain, et
        // renvoyer l'instant courant ferait boucler le planificateur sur la même échéance.
        Assert.Equal(new DateTime(2031, 3, 3, 12, 31, 0, DateTimeKind.Utc), Next("* * * * *", now));
    }

    [Fact]
    public void A_fixed_daily_time_rolls_over_to_tomorrow_once_it_has_passed()
    {
        var beforeNine = new DateTime(2031, 3, 3, 8, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2031, 3, 3, 9, 0, 0, DateTimeKind.Utc), Next("0 9 * * *", beforeNine));

        var afterNine = new DateTime(2031, 3, 3, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2031, 3, 4, 9, 0, 0, DateTimeKind.Utc), Next("0 9 * * *", afterNine));
    }

    [Fact]
    public void Steps_lists_and_ranges_all_resolve()
    {
        var now = new DateTime(2031, 3, 3, 10, 7, 0, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2031, 3, 3, 10, 15, 0, DateTimeKind.Utc), Next("*/15 * * * *", now));
        Assert.Equal(new DateTime(2031, 3, 3, 10, 30, 0, DateTimeKind.Utc), Next("0,30 * * * *", now));
        Assert.Equal(new DateTime(2031, 3, 3, 10, 10, 0, DateTimeKind.Utc), Next("10-20 * * * *", now));
        // Un pas sans borne haute court jusqu'au maximum du champ : 5, 15, 25… comme Vixie cron.
        Assert.Equal(new DateTime(2031, 3, 3, 10, 15, 0, DateTimeKind.Utc), Next("5/10 * * * *", now));
    }

    [Fact]
    public void Weekday_only_schedules_skip_the_weekend()
    {
        // Le vendredi 7 mars 2031 à 10 h : la prochaine occurrence est le lundi, pas le samedi.
        var friday = new DateTime(2031, 3, 7, 10, 0, 0, DateTimeKind.Utc);
        var next = Next("0 9 * * 1-5", friday);

        Assert.Equal(new DateTime(2031, 3, 10, 9, 0, 0, DateTimeKind.Utc), next);
        Assert.Equal(DayOfWeek.Monday, next.DayOfWeek);
    }

    [Fact]
    public void Sunday_is_both_zero_and_seven()
    {
        var start = new DateTime(2031, 3, 3, 0, 0, 0, DateTimeKind.Utc); // un lundi

        // Les deux notations coexistent dans la nature ; en refuser une ferait échouer des
        // expressions parfaitement valides copiées d'un crontab.
        Assert.Equal(Next("0 12 * * 0", start), Next("0 12 * * 7", start));
        Assert.Equal(DayOfWeek.Sunday, Next("0 12 * * 0", start).DayOfWeek);
    }

    [Fact]
    public void Month_and_day_names_are_accepted()
    {
        var start = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal(Next("0 12 1 3 *", start), Next("0 12 1 mar *", start));
        Assert.Equal(Next("0 12 * * 1", start), Next("0 12 * * mon", start));
    }

    /// <summary>
    /// La règle POSIX que tout le monde découvre en production : quand jour-du-mois ET
    /// jour-de-la-semaine sont tous deux restreints, ils s'ADDITIONNENT.
    /// </summary>
    [Fact]
    public void A_restricted_day_of_month_and_day_of_week_combine_as_a_union()
    {
        // `0 0 1 * 1` = le premier du mois, ET AUSSI tous les lundis.
        var start = new DateTime(2031, 3, 4, 0, 0, 0, DateTimeKind.Utc); // mardi 4 mars
        var next = Next("0 0 1 * 1", start);

        // Le lundi 10 mars arrive avant le 1er avril : l'intersection aurait rendu le 1er avril
        // seulement s'il tombait un lundi, c'est-à-dire presque jamais.
        Assert.Equal(new DateTime(2031, 3, 10, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void One_restricted_day_field_keeps_the_ordinary_conjunction()
    {
        // Jour de la semaine libre : seul le jour du mois compte.
        var start = new DateTime(2031, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2031, 4, 1, 0, 0, 0, DateTimeKind.Utc), Next("0 0 1 * *", start));
    }

    [Fact]
    public void A_schedule_is_evaluated_in_its_own_time_zone()
    {
        // 9 h à Paris début mars = 8 h UTC (UTC+1, heure d'hiver).
        var start = new DateTime(2031, 3, 3, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DateTime(2031, 3, 3, 8, 0, 0, DateTimeKind.Utc), Next("0 9 * * *", start, Paris));

        // La même expression en UTC ne donne évidemment pas le même instant. C'est toute la raison
        // d'être de la colonne `timezone` : « tous les jours à 9 h » ne veut rien dire sans elle.
        Assert.Equal(new DateTime(2031, 3, 3, 9, 0, 0, DateTimeKind.Utc), Next("0 9 * * *", start, Utc));
    }

    [Fact]
    public void The_same_wall_clock_time_shifts_in_utc_across_a_dst_boundary()
    {
        // Fin mars, Paris passe à UTC+2 : la même expression sort une heure plus tôt en UTC.
        var winter = Next("0 9 * * *", new DateTime(2031, 3, 20, 12, 0, 0, DateTimeKind.Utc), Paris);
        var summer = Next("0 9 * * *", new DateTime(2031, 4, 20, 12, 0, 0, DateTimeKind.Utc), Paris);

        Assert.Equal(8, winter.Hour);
        Assert.Equal(7, summer.Hour);
    }

    [Fact]
    public void A_local_time_that_does_not_exist_is_skipped_rather_than_invented()
    {
        // Le 30 mars 2031, Paris saute de 2 h à 3 h : 2 h 30 n'existe pas ce jour-là.
        var start = new DateTime(2031, 3, 29, 12, 0, 0, DateTimeKind.Utc);
        var next = Next("30 2 * * *", start, Paris);

        // L'occurrence du 30 est sautée ; la suivante est celle du 31, à 2 h 30 heure d'été
        // (0 h 30 UTC). Inventer une exécution serait la lancer à une heure que le calendrier
        // local ne connaît pas.
        Assert.Equal(new DateTime(2031, 3, 31, 0, 30, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void A_leap_day_schedule_waits_for_the_leap_year()
    {
        var start = new DateTime(2031, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var next = Next("0 0 29 2 *", start);

        // 2032 est bissextile ; 2031 ne l'est pas. La recherche doit donc porter sur plusieurs
        // années, ce qui est la seule raison pour laquelle sa profondeur dépasse un an.
        Assert.Equal(new DateTime(2032, 2, 29, 0, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void An_impossible_date_yields_no_occurrence_instead_of_looping_forever()
    {
        var schedule = CronSchedule.Parse("0 0 30 2 *"); // le 30 février
        Assert.Null(schedule.GetNextOccurrence(new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc), Utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]              // quatre champs
    [InlineData("* * * * * *")]          // six champs (secondes : non gérées, et c'est explicite)
    [InlineData("60 * * * *")]           // minute hors bornes
    [InlineData("* 24 * * *")]           // heure hors bornes
    [InlineData("* * 0 * *")]            // jour du mois commence à 1
    [InlineData("* * * 13 *")]           // mois hors bornes
    [InlineData("* * * * 8")]            // jour de semaine hors bornes
    [InlineData("*/0 * * * *")]          // pas nul
    [InlineData("20-10 * * * *")]        // intervalle inversé
    [InlineData("lundi * * * *")]        // nom inconnu
    public void Malformed_expressions_are_refused_at_parse_time(string expression)
    {
        // Refusées ici et non des heures plus tard, au premier réveil du planificateur, dans un
        // journal que personne ne lit.
        Assert.Throws<FormatException>(() => CronSchedule.Parse(expression));

        Assert.False(CronSchedule.TryParse(expression, out var schedule, out var error));
        Assert.Null(schedule);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void The_error_message_names_the_offending_field()
    {
        var error = Assert.Throws<FormatException>(() => CronSchedule.Parse("* 99 * * *"));

        // Un message qui dit seulement « expression invalide » oblige à deviner lequel des cinq
        // champs est en cause.
        Assert.Contains("99", error.Message);
    }

    [Fact]
    public void Surrounding_whitespace_is_tolerated_and_the_expression_is_normalized()
    {
        var schedule = CronSchedule.Parse("  0   9  *  *  *  ");

        Assert.Equal("0   9  *  *  *", schedule.Expression);
        Assert.Equal(
            new DateTime(2031, 3, 3, 9, 0, 0, DateTimeKind.Utc),
            schedule.GetNextOccurrence(new DateTime(2031, 3, 3, 0, 0, 0, DateTimeKind.Utc), Utc));
    }
}
