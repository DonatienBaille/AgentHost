using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>
/// The artifacts table has no org_id of its own, so tenant scoping joins through the owning run
/// (artifacts.run_id -&gt; runs.org_id). Every read below takes the caller's org and enforces it in
/// SQL; a miss returns null so the endpoint answers 404 rather than leaking existence via 403.
/// </summary>
public interface IArtifactRepository
{
    Task<Artifact?> GetAsync(string id, string orgId, CancellationToken ct = default);
    Task<List<Artifact>> ListByRunAsync(string runId, string orgId, CancellationToken ct = default);
    Task InsertAsync(Artifact artifact, CancellationToken ct = default);
}

public class ArtifactRepository : IArtifactRepository
{
    private const string SelectColumns = """
        a.id, a.run_id, a.name, a.artifact_type, a.s3_path, a.size_bytes, a.created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public ArtifactRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Artifact?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM artifacts a
            JOIN runs r ON r.id = a.run_id
            WHERE a.id = @Id AND r.org_id = @OrgId AND r.deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Artifact>(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
    }

    public async Task<List<Artifact>> ListByRunAsync(string runId, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM artifacts a
            JOIN runs r ON r.id = a.run_id
            WHERE a.run_id = @RunId AND r.org_id = @OrgId AND r.deleted_at IS NULL
            ORDER BY a.created_at ASC
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Artifact>(new CommandDefinition(sql, new { RunId = runId, OrgId = orgId }, cancellationToken: ct));
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
