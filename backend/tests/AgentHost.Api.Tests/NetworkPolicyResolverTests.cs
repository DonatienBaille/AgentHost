using AgentHost.Api.Services;
using Serilog;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// La politique réseau des conteneurs d'agents (spécification §13, lot 1.2).
///
/// Tous les cas qui comptent ici sont des **échecs fermés** : des situations où le manifeste
/// demande un filtrage qu'on ne sait pas appliquer, et où la bonne réponse est de couper le réseau
/// plutôt que d'accorder un accès non filtré. Une plateforme qui exécute du code tiers ne peut pas
/// se permettre de traiter « je n'ai pas su appliquer la règle » comme « la règle est satisfaite ».
///
/// Le cas ajouté par le lot 1.2 est celui du réseau interne manquant : jusque-là, une allowlist
/// sans réseau <c>--internal</c> retombait sur un bridge ordinaire où <c>HTTP_PROXY</c> n'est
/// qu'une convention — contraignante pour le code qui coopère, c'est-à-dire pas pour celui contre
/// lequel elle existe.
/// </summary>
public class NetworkPolicyResolverTests
{
    private static readonly ILogger Silent = new LoggerConfiguration().CreateLogger();

    private static readonly string[] Hosts = ["api.github.com", "registry.npmjs.org"];

    private static NetworkPolicyResolver.Settings Settings(
        string? proxy = "http://egress-proxy:3128",
        string? internalNetwork = "agenthost-egress",
        bool allowUnconfined = false) =>
        new(proxy, internalNetwork, "localhost,127.0.0.1,::1", allowUnconfined);

    private static NetworkPolicy Resolve(
        string? requested,
        IReadOnlyList<string>? allowlist = null,
        NetworkPolicyResolver.Settings? settings = null) =>
        NetworkPolicyResolver.Resolve(
            requested, allowlist ?? Hosts, "run-1", settings ?? Settings(), Silent);

    [Fact]
    public void Full_gets_an_unrestricted_bridge_and_no_proxy_environment()
    {
        var policy = Resolve("full");

        Assert.Equal("bridge", policy.Mode);
        Assert.True(policy.HasNetwork);
        Assert.Empty(policy.EnvVars);
    }

    [Fact]
    public void None_gets_no_interface_at_all()
    {
        var policy = Resolve("none");

        Assert.Equal("none", policy.Mode);
        Assert.False(policy.HasNetwork);
    }

    [Fact]
    public void An_unknown_mode_grants_nothing()
    {
        // Une faute de frappe dans un manifeste ne doit pas ouvrir le réseau.
        foreach (var mode in new string?[] { null, "", "allow-list", "ALLOWLIST", "internet" })
        {
            var policy = Resolve(mode);
            Assert.Equal("none", policy.Mode);
            Assert.False(policy.HasNetwork);
        }
    }

    [Fact]
    public void Allowlist_on_an_internal_network_gets_the_proxy_environment()
    {
        var policy = Resolve("allowlist");

        Assert.Equal("agenthost-egress", policy.Mode);
        Assert.True(policy.HasNetwork);

        // Les deux casses : les bibliothèques HTTP ne s'accordent pas sur celle qu'elles lisent.
        Assert.Contains("HTTP_PROXY=http://egress-proxy:3128", policy.EnvVars);
        Assert.Contains("http_proxy=http://egress-proxy:3128", policy.EnvVars);
        Assert.Contains("HTTPS_PROXY=http://egress-proxy:3128", policy.EnvVars);
        Assert.Contains("https_proxy=http://egress-proxy:3128", policy.EnvVars);
        Assert.Contains("NO_PROXY=localhost,127.0.0.1,::1", policy.EnvVars);
        Assert.Contains("AGENTHOST_NETWORK_ALLOWLIST=api.github.com,registry.npmjs.org", policy.EnvVars);
        Assert.Equal(Hosts, policy.Allowlist);
    }

    [Fact]
    public void Allowlist_without_an_egress_proxy_gets_no_network()
    {
        var policy = Resolve("allowlist", settings: Settings(proxy: null));

        Assert.Equal("none", policy.Mode);
        Assert.False(policy.HasNetwork);
        Assert.Empty(policy.EnvVars);
    }

    [Fact]
    public void Allowlist_with_an_empty_host_list_gets_no_network()
    {
        // Rien n'est autorisé : accorder un réseau serait accorder plus que ce qui est demandé.
        var policy = Resolve("allowlist", allowlist: []);

        Assert.Equal("none", policy.Mode);
        Assert.False(policy.HasNetwork);
    }

    /// <summary>Le cœur du lot 1.2.</summary>
    [Fact]
    public void Allowlist_without_an_internal_network_gets_no_network_by_default()
    {
        var policy = Resolve("allowlist", settings: Settings(internalNetwork: null));

        // Sur un bridge ordinaire, HTTP_PROXY n'est qu'une convention : un socket TCP brut sort
        // sans filtrage. Accorder le réseau ici reviendrait à annoncer une allowlist qui n'en est
        // pas une.
        Assert.Equal("none", policy.Mode);
        Assert.False(policy.HasNetwork);
        Assert.Empty(policy.EnvVars);
    }

    [Fact]
    public void An_operator_can_opt_back_into_advisory_only_filtering()
    {
        // Un déploiement existant qui s'en accommodait ne doit pas perdre son réseau au premier
        // redémarrage — mais il doit le demander explicitement.
        var policy = Resolve("allowlist", settings: Settings(internalNetwork: null, allowUnconfined: true));

        Assert.Equal("bridge", policy.Mode);
        Assert.True(policy.HasNetwork);
        Assert.Contains("HTTP_PROXY=http://egress-proxy:3128", policy.EnvVars);
    }

    [Fact]
    public void The_opt_out_does_not_rescue_a_missing_proxy()
    {
        // AllowUnconfined ne concerne que l'absence de réseau interne. Sans proxy il n'y a
        // strictement rien qui filtre, et aucun réglage ne doit accorder le réseau.
        var policy = Resolve("allowlist", settings: Settings(proxy: null, internalNetwork: null, allowUnconfined: true));

        Assert.Equal("none", policy.Mode);
        Assert.False(policy.HasNetwork);
    }

    [Fact]
    public void The_opt_out_does_not_rescue_an_empty_allowlist()
    {
        var policy = Resolve("allowlist", allowlist: [],
            settings: Settings(internalNetwork: null, allowUnconfined: true));

        Assert.Equal("none", policy.Mode);
        Assert.False(policy.HasNetwork);
    }
}
