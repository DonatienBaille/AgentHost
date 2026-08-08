using System.Text.Json;
using Serilog;
using StackExchange.Redis;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Un cache de courte durée pour les agrégats servis à l'IHM (feuille de route, lot 4, phase P3).
///
/// <b>Pourquoi Redis n'était encore qu'un backplane.</b> La spécification le range dans
/// « cache/sessions » et le déploiement en fournit un depuis l'origine, mais la seule chose qui
/// s'en servait était le backplane SignalR : la ligne « cache Redis réellement utilisé » de la
/// feuille de route désignait exactement cet écart entre une dépendance déployée et une dépendance
/// utile.
///
/// <b>Ce qui est mis en cache, et pourquoi c'est celui-là.</b> Les quatre agrégats du tableau de
/// bord balayent la table `runs` sur une fenêtre de trente à quatre-vingt-dix jours, et l'écran
/// porte un bouton « Actualiser ». Recalculer quatre agrégations complètes à chaque coup d'œil est
/// le cas d'école du travail refait à l'identique. Rien d'autre n'est mis en cache : ni un run, ni
/// un agent, ni un secret — un cache sur des données qu'on lit pour agir doit être invalidé, et une
/// invalidation oubliée est un bug bien plus coûteux que la lecture qu'elle économisait.
///
/// <b>Expiration seule, pas d'invalidation.</b> Un tableau de bord sur trente jours est une aide à
/// la décision, pas une console temps réel : trente secondes de retard y sont invisibles.
/// Invalider à chaque transition de run rendrait au contraire le cache inutile précisément quand il
/// sert — sur une organisation active, où les transitions sont continues.
///
/// <b>Un cache ne doit jamais casser la page.</b> Toute erreur Redis — indisponibilité, timeout,
/// charge illisible — retombe sur le calcul direct et n'est que journalisée. C'est le même contrat
/// que le reste du dépôt applique à Redis : optionnel, jamais bloquant.
/// </summary>
public interface IAggregateCache
{
    /// <summary>Faux quand aucun Redis n'est joignable : tout passe alors directement au calcul.</summary>
    bool IsEnabled { get; }

    /// <summary>Rend la valeur en cache, ou la calcule, la range et la rend.</summary>
    Task<T> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory, CancellationToken ct = default);
}

/// <summary>
/// L'implémentation dégradée : aucun Redis configuré ou joignable, donc aucun cache.
///
/// Le comportement est <b>exactement</b> celui d'avant l'introduction du cache. C'est ce qui permet
/// de considérer le cache comme une optimisation à laquelle un déploiement souscrit, et non comme
/// une pièce dont l'absence change les réponses.
/// </summary>
public sealed class NoAggregateCache : IAggregateCache
{
    public bool IsEnabled => false;

    public Task<T> GetOrSetAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory, CancellationToken ct = default) =>
        factory();
}

/// <summary>Le cache adossé à Redis. Voir <see cref="IAggregateCache"/> pour les arbitrages.</summary>
public sealed class RedisAggregateCache : IAggregateCache
{
    /// <summary>
    /// Préfixe de toutes les clés, avec un numéro de version.
    ///
    /// La version existe pour un déploiement : changer la forme d'une réponse rend les entrées
    /// existantes indésérialisables. L'incrémenter fait ignorer d'un coup tout l'ancien contenu,
    /// sans SCAN ni suppression de masse sur un Redis potentiellement partagé — les orphelines
    /// s'éteignent d'elles-mêmes avec leur expiration.
    /// </summary>
    public const string KeyPrefix = "agenthost:v1:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger _logger;

    public RedisAggregateCache(IConnectionMultiplexer redis, ILogger logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public bool IsEnabled => _redis.IsConnected;

    public async Task<T> GetOrSetAsync<T>(
        string key, TimeSpan ttl, Func<Task<T>> factory, CancellationToken ct = default)
    {
        var fullKey = KeyPrefix + key;

        try
        {
            var cached = await _redis.GetDatabase().StringGetAsync(fullKey);
            if (cached.HasValue)
            {
                var value = JsonSerializer.Deserialize<T>(cached!, Json);
                // `null` désérialisé n'est pas un succès de cache : on recalcule plutôt que de
                // servir un vide qu'on prendrait pour une réponse.
                if (value is not null) return value;
            }
        }
        catch (Exception ex) when (ex is RedisException or JsonException or TimeoutException)
        {
            // Journalisé une fois, puis oublié : un cache qui fait échouer la lecture qu'il devait
            // accélérer est pire que pas de cache du tout.
            _logger.Warning(ex, "Aggregate cache read failed for {Key}; falling back to the query", fullKey);
        }

        var computed = await factory();

        try
        {
            await _redis.GetDatabase().StringSetAsync(
                fullKey, JsonSerializer.Serialize(computed, Json), ttl);
        }
        catch (Exception ex) when (ex is RedisException or JsonException or TimeoutException)
        {
            _logger.Warning(ex, "Aggregate cache write failed for {Key}", fullKey);
        }

        return computed;
    }
}
