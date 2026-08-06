using System.Security.Cryptography;
using System.Text;

namespace AgentHost.Runner;

/// <summary>
/// Le jeton porteur partagé qui protège l'API du runner (<c>Runner:AuthToken</c>).
///
/// <para><b>Ce n'est pas une API publique.</b> Elle lance des conteneurs sur le nœud : quiconque
/// l'atteint dispose de tout ce que le socket du démon permet. Le jeton n'est donc pas une
/// « authentification d'utilisateur » — il n'y a pas d'utilisateur, pas de rôle, pas de tenant ici —
/// mais la preuve que l'appelant est le backend. La défense principale reste le réseau : le runner
/// n'écoute que sur le réseau interne du cluster et la NetworkPolicy du chart n'autorise que le
/// backend à l'atteindre. Le jeton est la seconde barrière, celle qui tient quand la première a été
/// mal configurée.</para>
///
/// <para><b>Échec fermé au démarrage.</b> Sans jeton configuré, le processus refuse de démarrer.
/// L'alternative — démarrer sans authentification en journalisant un avertissement — donne un
/// runner ouvert dans tout déploiement où quelqu'un a oublié le réglage, et l'avertissement n'est
/// jamais lu.</para>
///
/// <para>La comparaison est à temps constant : un jeton comparé avec <c>==</c> se devine octet par
/// octet à la mesure du temps de réponse, et ce point d'entrée est justement celui qu'on ne peut pas
/// se permettre de laisser deviner.</para>
/// </summary>
public static class RunnerAuthentication
{
    public const string ConfigurationKey = "Runner:AuthToken";

    /// <summary>Longueur minimale acceptée : un jeton court n'apporte rien qu'une NetworkPolicy n'apporte déjà.</summary>
    public const int MinimumTokenLength = 16;

    public static string RequireToken(IConfiguration config)
    {
        var token = config[ConfigurationKey];

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} is not configured. The runner launches containers on this node; " +
                "starting it without a shared bearer token would expose that to anything that can " +
                "reach the port. Generate one with `openssl rand -base64 32`.");
        }

        if (token.Trim().Length < MinimumTokenLength)
        {
            throw new InvalidOperationException(
                $"{ConfigurationKey} must be at least {MinimumTokenLength} characters.");
        }

        return token.Trim();
    }

    /// <summary>
    /// Exige le jeton sur tout le groupe de routes. Renvoie 401 sans corps : un message détaillant
    /// ce qui manque ne sert qu'à celui qui n'a rien à faire là.
    /// </summary>
    public static TBuilder RequireRunnerToken<TBuilder>(this TBuilder builder, string expectedToken)
        where TBuilder : IEndpointConventionBuilder
    {
        var expected = Encoding.UTF8.GetBytes(expectedToken);

        builder.AddEndpointFilter(async (context, next) =>
        {
            var header = context.HttpContext.Request.Headers.Authorization.ToString();

            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            var presented = Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim());

            // FixedTimeEquals renvoie faux immédiatement sur une longueur différente ; la longueur du
            // jeton n'est pas un secret, son contenu l'est.
            if (presented.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(presented, expected))
                return Results.Unauthorized();

            return await next(context);
        });

        return builder;
    }
}
