using AgentHost.Api.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace AgentHost.Api.Tests;

/// <summary>
/// Un <see cref="AgentHostMetrics"/> réel, branché sur un <c>IMeterFactory</c> jetable.
///
/// Un double ne servirait à rien ici : ces tests ne portent pas sur les métriques, et un vrai
/// instrument sans collecteur n'écrit nulle part. En revanche il **s'exécute**, ce qui garde le
/// chemin d'émission compilé et couvert — une exception dans le calcul d'une étiquette casserait
/// le test qui l'a déclenchée, ce qui est exactement le retour que l'on veut.
/// </summary>
public static class TestMetrics
{
    public static AgentHostMetrics Create() =>
        new(new ServiceCollection().AddMetrics().BuildServiceProvider()
            .GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>());
}
