using AgentHost.Api.Services;
using AgentHost.Runner;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHost.Api.Tests.Runner;

/// <summary>
/// The default is the whole point of these: an existing installation that upgrades its binary must
/// keep the in-process orchestrator, and a deployment that asks for the runner tier must not be
/// allowed to half-configure it.
/// </summary>
public class RunnerModeTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void InProcessIsTheDefaultWhenNothingIsConfigured()
    {
        var options = RunnerOptions.FromConfiguration(Config());

        Assert.Equal(RunnerMode.InProcess, options.Mode);
        Assert.False(options.IsRemote);
    }

    [Theory]
    [InlineData("")]
    [InlineData("inprocess")]
    [InlineData("in-process")]
    [InlineData("Remote ")]        // espace de fin : accepté, c'est le même mot
    [InlineData("REMOTE")]         // casse : accepté aussi
    public void ModeParsingIsForgivingOnlyAboutSpellingsOfTheSameWord(string raw)
    {
        var expectRemote = raw.Trim().Equals("remote", StringComparison.OrdinalIgnoreCase);

        var options = RunnerOptions.FromConfiguration(Config(
            ("Runner:Mode", raw),
            ("Runner:BaseUrl", "http://runner:5001"),
            ("Runner:AuthToken", "token-0123456789abcdef")));

        Assert.Equal(expectRemote ? RunnerMode.Remote : RunnerMode.InProcess, options.Mode);
    }

    [Theory]
    [InlineData("distant")]
    [InlineData("runner")]
    [InlineData("true")]
    public void AnUnknownModeFallsBackToInProcessRatherThanFailing(string raw)
    {
        // Une faute de frappe ne doit pas empêcher le démarrage, mais elle ne doit surtout pas
        // activer le tier runner : le défaut sûr est celui qui marche partout.
        var options = RunnerOptions.FromConfiguration(Config(("Runner:Mode", raw)));

        Assert.Equal(RunnerMode.InProcess, options.Mode);
    }

    [Fact]
    public void RemoteModeWithoutABaseUrlRefusesToStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RunnerOptions.FromConfiguration(Config(
                ("Runner:Mode", "remote"),
                ("Runner:AuthToken", "token-0123456789abcdef"))));

        Assert.Contains("Runner:BaseUrl", ex.Message);
    }

    [Fact]
    public void RemoteModeWithoutAnAuthTokenRefusesToStart()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RunnerOptions.FromConfiguration(Config(
                ("Runner:Mode", "remote"),
                ("Runner:BaseUrl", "http://runner:5001"))));

        Assert.Contains("Runner:AuthToken", ex.Message);
    }

    [Fact]
    public void TheBaseUrlLosesItsTrailingSlashSoRoutesAreNotDoubled()
    {
        var options = RunnerOptions.FromConfiguration(Config(
            ("Runner:Mode", "remote"),
            ("Runner:BaseUrl", "http://runner:5001/"),
            ("Runner:AuthToken", "token-0123456789abcdef")));

        Assert.Equal("http://runner:5001", options.BaseUrl);
    }

    // ---- Le runner refuse de démarrer sans jeton ----------------------------------------------

    [Fact]
    public void TheRunnerRefusesToStartWithoutAnAuthToken()
    {
        // Démarrer en journalisant un avertissement donnerait un runner ouvert — c'est-à-dire un
        // accès root au nœud — dans tout déploiement où le réglage a été oublié.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RunnerAuthentication.RequireToken(Config()));

        Assert.Contains("Runner:AuthToken", ex.Message);
    }

    [Fact]
    public void TheRunnerRefusesToStartWithATokenTooShortToBeWorthAnything()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => RunnerAuthentication.RequireToken(Config(("Runner:AuthToken", "short"))));

        Assert.Contains("at least", ex.Message);
    }

    [Fact]
    public void AConfiguredTokenIsAcceptedAndTrimmed()
    {
        var token = RunnerAuthentication.RequireToken(Config(("Runner:AuthToken", "  token-0123456789abcdef  ")));

        Assert.Equal("token-0123456789abcdef", token);
    }
}
