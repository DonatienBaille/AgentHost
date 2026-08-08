using AgentHost.Api.Infrastructure;
using Serilog;
using StackExchange.Redis;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// Sonde unique d'un Redis joignable, résolue comme le produit le résout.
///
/// Le cache est optionnel par contrat : un test qui « passe » sans Redis ne prouverait rien, et un
/// test qui échoue faute de Redis accuserait le code d'un défaut d'environnement. On saute donc
/// explicitement, en disant pourquoi.
/// </summary>
public static class RedisProbe
{
    private static readonly Lazy<string?> Probe = new(Check, isThreadSafe: true);

    public static string? SkipReason => Probe.Value;

    public static string Configuration =>
        Environment.GetEnvironmentVariable("REDIS_HOST") is { Length: > 0 } host
            ? $"{host}:6379"
            : "localhost:6379";

    private static string? Check()
    {
        try
        {
            using var connection = ConnectionMultiplexer.Connect(
                new ConfigurationOptions
                {
                    EndPoints = { Configuration },
                    AbortOnConnectFail = false,
                    ConnectTimeout = 1500,
                });
            return connection.IsConnected ? null : $"No Redis reachable at {Configuration}";
        }
        catch (Exception ex)
        {
            return $"No Redis reachable at {Configuration}: {ex.Message}";
        }
    }
}

/// <summary>
/// Un <c>[Fact]</c> qui se saute — jamais qui passe en silence — quand aucun Redis ne répond.
/// Même dispositif que <c>DockerFactAttribute</c> pour le démon de conteneurs.
/// </summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public override string? Skip
    {
        get => RedisProbe.SkipReason ?? base.Skip;
        set => base.Skip = value;
    }
}

/// <summary>
/// Le cache d'agrégats (feuille de route, lot 4, phase P3).
///
/// <b>Ce que ces tests protègent.</b> Un cache est du code dont l'effet est de ne PAS exécuter du
/// code : ses défauts ne se manifestent pas par une erreur mais par une réponse plausible et
/// périmée, ou pire, par la réponse d'un autre locataire. Deux propriétés dominent donc — la clé
/// sépare réellement ce qu'elle prétend séparer, et une panne de Redis ne casse jamais la lecture
/// qu'elle devait accélérer.
/// </summary>
public class AggregateCacheTests
{
    private sealed record Payload(int Value);

    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    // ---- l'implémentation dégradée ----

    [Fact]
    public async Task Without_redis_every_read_recomputes_and_nothing_changes()
    {
        var cache = new NoAggregateCache();
        var calls = 0;

        for (var i = 0; i < 3; i++)
            await cache.GetOrSetAsync("k", TimeSpan.FromMinutes(1), () => { calls++; return Task.FromResult(new Payload(1)); });

        // Le comportement doit être EXACTEMENT celui d'avant l'introduction du cache : c'est ce qui
        // permet de le considérer comme une optimisation à laquelle on souscrit, et non comme une
        // pièce dont l'absence change les réponses.
        Assert.Equal(3, calls);
        Assert.False(cache.IsEnabled);
    }

    // ---- l'implémentation Redis ----

    [RedisFact]
    public async Task A_second_read_within_the_ttl_does_not_recompute()
    {
        var (cache, key) = await CacheAsync();
        var calls = 0;

        var first = await cache.GetOrSetAsync(key, TimeSpan.FromMinutes(1), () => { calls++; return Task.FromResult(new Payload(42)); });
        var second = await cache.GetOrSetAsync(key, TimeSpan.FromMinutes(1), () => { calls++; return Task.FromResult(new Payload(99)); });

        Assert.Equal(42, first.Value);
        // La seconde lecture rend la valeur mémorisée, pas celle que la fabrique aurait produite :
        // c'est la seule preuve que la fabrique n'a pas tourné.
        Assert.Equal(42, second.Value);
        Assert.Equal(1, calls);
    }

    [RedisFact]
    public async Task Two_keys_never_share_a_value()
    {
        var (cache, key) = await CacheAsync();

        var a = await cache.GetOrSetAsync($"{key}:a", TimeSpan.FromMinutes(1), () => Task.FromResult(new Payload(1)));
        var b = await cache.GetOrSetAsync($"{key}:b", TimeSpan.FromMinutes(1), () => Task.FromResult(new Payload(2)));

        // La clé des agrégats porte l'organisation ET la fenêtre. Deux locataires qui partageraient
        // une clé partageraient leurs chiffres — c'est la fuite que ce test existe pour interdire.
        Assert.Equal(1, a.Value);
        Assert.Equal(2, b.Value);
    }

    [RedisFact]
    public async Task An_expired_entry_is_recomputed()
    {
        var (cache, key) = await CacheAsync();
        var calls = 0;

        await cache.GetOrSetAsync(key, TimeSpan.FromMilliseconds(200), () => { calls++; return Task.FromResult(new Payload(1)); });
        await Task.Delay(500);
        var after = await cache.GetOrSetAsync(key, TimeSpan.FromMinutes(1), () => { calls++; return Task.FromResult(new Payload(2)); });

        // L'expiration est le seul mécanisme d'invalidation : il n'y en a pas d'autre, et il doit
        // donc marcher.
        Assert.Equal(2, after.Value);
        Assert.Equal(2, calls);
    }

    [RedisFact]
    public async Task Every_key_is_namespaced_so_a_shared_redis_stays_shareable()
    {
        var (cache, key) = await CacheAsync();

        await cache.GetOrSetAsync(key, TimeSpan.FromMinutes(1), () => Task.FromResult(new Payload(7)));

        using var connection = await ConnectAsync();
        // Le préfixe versionné n'est pas cosmétique : il permet de changer la forme d'une réponse
        // sans SCAN ni suppression de masse sur un Redis que d'autres applications utilisent.
        //
        // Les deux assertions sont nécessaires et aucune ne suffit : la première dit que la clé est
        // écrite là où la constante l'annonce, la seconde que cette constante est bien un espace de
        // noms — sans elle, vider le préfixe passerait inaperçu puisque le test le réutilise.
        Assert.True(await connection.GetDatabase().KeyExistsAsync(RedisAggregateCache.KeyPrefix + key));
        Assert.StartsWith("agenthost:v", RedisAggregateCache.KeyPrefix);
    }

    [RedisFact]
    public async Task A_broken_redis_falls_back_to_the_query_instead_of_failing_the_read()
    {
        // Un multiplexeur pointé vers un port fermé : le cache doit se comporter comme s'il
        // n'existait pas. Un cache qui fait échouer la lecture qu'il devait accélérer est pire que
        // pas de cache du tout.
        using var dead = await ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { "127.0.0.1:6399" },
            AbortOnConnectFail = false,
            ConnectTimeout = 300,
            ConnectRetry = 0,
        });

        var cache = new RedisAggregateCache(dead, Logger);
        var value = await cache.GetOrSetAsync("dead", TimeSpan.FromMinutes(1), () => Task.FromResult(new Payload(5)));

        Assert.Equal(5, value.Value);
        Assert.False(cache.IsEnabled);
    }

    // ---- helpers ----

    private static Task<ConnectionMultiplexer> ConnectAsync() =>
        ConnectionMultiplexer.ConnectAsync(new ConfigurationOptions
        {
            EndPoints = { RedisProbe.Configuration },
            AbortOnConnectFail = false,
        });

    /// <summary>Un cache et une clé unique : les tests partagent un Redis qu'ils ne doivent pas se voler.</summary>
    private static async Task<(RedisAggregateCache Cache, string Key)> CacheAsync()
    {
        var connection = await ConnectAsync();
        return (new RedisAggregateCache(connection, Logger), $"test:{Guid.NewGuid():N}");
    }
}
