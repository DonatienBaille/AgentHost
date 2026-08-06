using Docker.DotNet;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentHost.Runner;

/// <summary>
/// Sonde de disponibilité du runner : le démon de conteneurs répond-il ?
///
/// <para>C'est la seule dépendance du runner, et c'est aussi la seule chose qu'il sait faire. Un pod
/// runner dont le socket ne répond pas doit sortir des points de terminaison du service : sinon le
/// backend continue de lui envoyer des lancements qui échoueront tous, alors qu'un autre nœud aurait
/// pu les prendre.</para>
/// </summary>
public sealed class ContainerRuntimeHealthCheck : IHealthCheck
{
    private readonly DockerClient _docker;

    public ContainerRuntimeHealthCheck(DockerClient docker) => _docker = docker;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await _docker.System.PingAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Container runtime did not answer", ex);
        }
    }
}
