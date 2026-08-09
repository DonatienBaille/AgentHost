using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using AgentHost.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// Les règles d'alerte d'exploitation (<c>deploy/observability/alerts.yml</c>), confrontées aux
/// instruments que le backend expose réellement (feuille de route, lot 4).
///
/// <b>Pourquoi un fichier de configuration mérite des tests.</b> Une règle d'alerte qui interroge
/// une série inexistante ne produit aucune erreur : elle produit <b>zéro</b>, indéfiniment, et donc
/// une alerte qui ne se déclenche jamais. C'est le pire des deux mondes — l'exploitant croit être
/// couvert. Renommer un instrument, changer son unité, ou en ajouter un sans le brancher : les
/// trois passent silencieusement, et ne se découvrent qu'à l'incident qu'on croyait surveillé.
///
/// <b>La liste des instruments n'est pas recopiée ici.</b> Elle est obtenue d'un
/// <see cref="MeterListener"/> sur un <see cref="AgentHostMetrics"/> réel — le mécanisme même de
/// l'exporteur. Une constante recopiée divergerait au premier ajout, ce qui est exactement le
/// défaut que ce test existe pour attraper.
/// </summary>
public class OperationalAlertsTests
{
    /// <summary>
    /// Instruments délibérément absents des alertes, et pourquoi.
    ///
    /// La liste est une <b>décision</b>, pas une dérogation : tout instrument qui n'y figure pas et
    /// qu'aucune règle n'utilise fait échouer le test. C'est ainsi qu'un instrument ajouté sans être
    /// branché se signale, au lieu d'attendre l'incident.
    /// </summary>
    private static readonly Dictionary<string, string> DeliberatelyNotAlerted = new()
    {
        ["agenthost.run.cost"] =
            "Le coût est un événement métier, remonté dans l'IHM de l'organisation concernée. " +
            "Réveiller un exploitant parce qu'un client dépense son propre argent serait une " +
            "astreinte sur une décision qui ne lui appartient pas.",
    };

    private static readonly string RulesPath = Path.Combine(
        RepositoryRoot(), "deploy", "observability", "alerts.yml");

    [Fact]
    public void The_rules_file_exists_and_is_not_empty()
    {
        Assert.True(File.Exists(RulesPath), $"Alert rules not found at {RulesPath}");
        Assert.NotEmpty(File.ReadAllText(RulesPath).Trim());
    }

    [Fact]
    public void Every_metric_the_rules_query_is_one_the_backend_actually_exposes()
    {
        var declared = DeclaredInstrumentNames();
        var referenced = ReferencedInstrumentNames();

        Assert.NotEmpty(referenced);

        // Une série inexistante ne lève pas d'erreur : elle vaut zéro pour toujours, et l'alerte
        // ne part jamais. C'est le défaut que rien d'autre ne peut attraper.
        var unknown = referenced.Except(declared).ToList();
        Assert.True(unknown.Count == 0,
            $"Les règles interrogent des instruments inconnus : {string.Join(", ", unknown)}. " +
            $"Instruments déclarés : {string.Join(", ", declared.OrderBy(n => n))}");
    }

    [Fact]
    public void Every_instrument_is_either_alerted_on_or_deliberately_excluded()
    {
        var declared = DeclaredInstrumentNames();
        var referenced = ReferencedInstrumentNames();

        var orphans = declared.Except(referenced).Except(DeliberatelyNotAlerted.Keys).ToList();

        // Ajouter un instrument sans décider s'il mérite une alerte, c'est instrumenter pour la
        // forme. Le test force la décision — et l'exclusion doit être écrite, avec sa raison.
        Assert.True(orphans.Count == 0,
            $"Instruments sans alerte ni exclusion documentée : {string.Join(", ", orphans)}. " +
            "Ajouter une règle dans deploy/observability/alerts.yml, ou une entrée dans " +
            "DeliberatelyNotAlerted avec la raison.");
    }

    [Fact]
    public void Every_rule_carries_a_for_clause_and_a_severity()
    {
        var text = File.ReadAllText(RulesPath);
        var alerts = Regex.Matches(text, @"- alert:\s*(\S+)").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(alerts);
        // Une alerte sans `for` se déclenche sur un point isolé — donc sur du bruit — et devient
        // une alerte qu'on apprend à ignorer, ce qui est pire que pas d'alerte du tout.
        Assert.Equal(alerts.Count, Regex.Matches(text, @"^\s+for:\s", RegexOptions.Multiline).Count);
        Assert.Equal(alerts.Count, Regex.Matches(text, @"^\s+severity:\s", RegexOptions.Multiline).Count);
        // Et un résumé : une alerte qui arrive sans phrase oblige à lire la requête pour savoir ce
        // qui se passe, à l'heure où on le sait le moins.
        Assert.Equal(alerts.Count, Regex.Matches(text, @"^\s+summary:\s", RegexOptions.Multiline).Count);
    }

    // ---- helpers ----

    /// <summary>
    /// Les instruments réellement publiés par <see cref="AgentHostMetrics"/>, sous leur nom pointé.
    ///
    /// Lus par le même mécanisme que l'exporteur, et filtrés sur le Meter par identité : un
    /// <see cref="MeterListener"/> est global au processus, et xUnit exécute les classes en
    /// parallèle.
    /// </summary>
    private static HashSet<string> DeclaredInstrumentNames()
    {
        using var provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        var factory = provider.GetRequiredService<IMeterFactory>();

        var names = new HashSet<string>();
        using var listener = new MeterListener();
        var meter = factory.Create(AgentHostMetrics.MeterName);

        listener.InstrumentPublished = (instrument, _) =>
        {
            if (ReferenceEquals(instrument.Meter, meter)) names.Add(instrument.Name);
        };
        listener.Start();

        // Les instruments ne sont publiés qu'à leur création : le listener doit exister avant.
        _ = new AgentHostMetrics(factory);

        return names;
    }

    /// <summary>
    /// Les instruments qu'interrogent les règles, ramenés de la forme Prometheus à la forme pointée.
    ///
    /// L'exporteur Prometheus transforme <c>agenthost.run.duration</c> (unité <c>s</c>) en
    /// <c>agenthost_run_duration_seconds_bucket</c> : point → tiret bas, unité connue suffixée,
    /// puis le suffixe de famille. On défait ces trois couches, dans l'ordre inverse.
    /// </summary>
    private static HashSet<string> ReferencedInstrumentNames()
    {
        var text = File.ReadAllText(RulesPath);
        var names = new HashSet<string>();

        foreach (Match match in Regex.Matches(text, @"\bagenthost_[a-z0-9_]+\b"))
        {
            var series = match.Value;

            foreach (var family in new[] { "_bucket", "_sum", "_count", "_total" })
            {
                if (series.EndsWith(family, StringComparison.Ordinal))
                {
                    series = series[..^family.Length];
                    break;
                }
            }

            if (series.EndsWith("_seconds", StringComparison.Ordinal))
                series = series[..^"_seconds".Length];

            // Le nom pointé ne peut pas se reconstituer par simple substitution — `run_in_flight`
            // vient de `run.in_flight`, pas de `run.in.flight`. On compare donc contre les noms
            // déclarés, dont on connaît la forme.
            names.Add(series);
        }

        // Repasse : associer chaque série au nom pointé qui lui correspond.
        var declared = DeclaredInstrumentNames();
        return names
            .Select(series => declared.FirstOrDefault(d => d.Replace('.', '_') == series) ?? series)
            .ToHashSet();
    }

    /// <summary>Remonte jusqu'à la racine du dépôt depuis le répertoire de sortie des tests.</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "deploy")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory");
    }
}
