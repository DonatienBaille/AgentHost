using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Readiness probe dependency: the API is useless without Postgres, so <c>/health/ready</c> only
/// reports healthy when a connection can actually be opened and a trivial statement executed.
/// Liveness (<c>/health/live</c>) deliberately does NOT run this — a database blip must not make
/// Kubernetes restart every backend pod.
/// </summary>
public class DatabaseHealthCheck : IHealthCheck
{
    private readonly IDbConnectionFactory _connectionFactory;

    public DatabaseHealthCheck(IDbConnectionFactory connectionFactory) => _connectionFactory = connectionFactory;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // CreateConnection() opens the connection eagerly, so this covers connect + query.
            using var connection = _connectionFactory.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.ExecuteScalar();

            return Task.FromResult(HealthCheckResult.Healthy("database reachable"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("database unreachable", ex));
        }
    }
}
