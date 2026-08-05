using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IArtifactRepository
{
    Task<Artifact?> GetAsync(string id, CancellationToken ct = default);
    Task<List<Artifact>> ListByRunAsync(string runId, CancellationToken ct = default);
    Task InsertAsync(Artifact artifact, CancellationToken ct = default);
}

public class ArtifactRepository : IArtifactRepository
{
    private const string SelectColumns = "id, run_id, name, artifact_type, s3_path, size_bytes, created_at";

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public ArtifactRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Artifact?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM artifacts WHERE id = @Id";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Artifact>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<List<Artifact>> ListByRunAsync(string runId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM artifacts WHERE run_id = @RunId ORDER BY created_at ASC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Artifact>(new CommandDefinition(sql, new { RunId = runId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task InsertAsync(Artifact artifact, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO artifacts (id, run_id, name, artifact_type, s3_path, size_bytes, created_at)
            VALUES (@Id, @RunId, @Name, @ArtifactType, @S3Path, @SizeBytes, @CreatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, artifact, cancellationToken: ct));
        _logger.Information("Inserted artifact {ArtifactId} for run {RunId}", artifact.Id, artifact.RunId);
    }
}
