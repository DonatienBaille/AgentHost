using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface ISecretRepository
{
    /// <summary>Org-scoped lookup: returns null (=&gt; 404, never 403) for another tenant's secret.</summary>
    Task<Secret?> GetAsync(string id, string orgId, CancellationToken ct = default);

    /// <summary>Org-scoped metadata listing. Callers must project away the ciphertext before responding.</summary>
    Task<List<Secret>> ListByOrgAsync(string orgId, int skip = 0, int take = 200, CancellationToken ct = default);

    Task<Secret?> GetByNameAsync(string orgId, string name, CancellationToken ct = default);
    Task<List<Secret>> ListForScopeAsync(string orgId, string? projectId, IEnumerable<string> names, CancellationToken ct = default);
    Task InsertAsync(Secret secret, CancellationToken ct = default);
    Task UpdateAsync(Secret secret, CancellationToken ct = default);
    Task MarkUsedAsync(string id, string runId, CancellationToken ct = default);
    Task SoftDeleteAsync(string id, string orgId, CancellationToken ct = default);
}

public class SecretRepository : ISecretRepository
{
    private const string SelectColumns = """
        id, org_id, project_id, name, encrypted_value, vault_path, digest_sha256_truncated,
        scope, last_used_at, last_used_by_run_id, created_at, updated_at, deleted_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public SecretRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Secret?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM secrets WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<SecretRow>(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        return row?.ToDomain();
    }

    public async Task<List<Secret>> ListByOrgAsync(string orgId, int skip = 0, int take = 200, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM secrets
            WHERE org_id = @OrgId AND deleted_at IS NULL
            ORDER BY created_at DESC
            LIMIT @Take OFFSET @Skip
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<SecretRow>(new CommandDefinition(
            sql,
            new { OrgId = orgId, Skip = Paging.ClampSkip(skip), Take = Paging.ClampTake(take) },
            cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<Secret?> GetByNameAsync(string orgId, string name, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM secrets WHERE org_id = @OrgId AND name = @Name AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<SecretRow>(new CommandDefinition(sql, new { OrgId = orgId, Name = name }, cancellationToken: ct));
        return row?.ToDomain();
    }

    /// <summary>
    /// Secrets visible to a run: org-scoped secrets, plus project-scoped secrets for the given
    /// project, restricted to the names the agent manifest declares in permissions.secrets.
    /// </summary>
    public async Task<List<Secret>> ListForScopeAsync(string orgId, string? projectId, IEnumerable<string> names, CancellationToken ct = default)
    {
        var nameList = names.ToList();
        if (nameList.Count == 0) return new List<Secret>();

        var sql = $"""
            SELECT {SelectColumns} FROM secrets
            WHERE org_id = @OrgId
              AND deleted_at IS NULL
              AND name = ANY(@Names)
              AND (scope = 'org' OR (scope = 'project' AND project_id = @ProjectId))
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<SecretRow>(new CommandDefinition(sql, new { OrgId = orgId, ProjectId = projectId, Names = nameList.ToArray() }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task InsertAsync(Secret secret, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO secrets (
                id, org_id, project_id, name, encrypted_value, vault_path, digest_sha256_truncated,
                scope, created_at, updated_at
            ) VALUES (
                @Id, @OrgId, @ProjectId, @Name, @EncryptedValue, @VaultPath, @DigestSha256Truncated,
                @Scope, @CreatedAt, @UpdatedAt
            )
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, SecretRow.FromDomain(secret), cancellationToken: ct));
        _logger.Information("Inserted secret {SecretId} ({Name})", secret.Id, secret.Name);
    }

    public async Task UpdateAsync(Secret secret, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE secrets
            SET encrypted_value = @EncryptedValue,
                vault_path = @VaultPath,
                digest_sha256_truncated = @DigestSha256Truncated,
                updated_at = @UpdatedAt
            WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, SecretRow.FromDomain(secret), cancellationToken: ct));
        _logger.Information("Updated secret {SecretId}", secret.Id);
    }

    public async Task MarkUsedAsync(string id, string runId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE secrets
            SET last_used_at = NOW(), last_used_by_run_id = @RunId
            WHERE id = @Id
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id, RunId = runId }, cancellationToken: ct));
    }

    public async Task SoftDeleteAsync(string id, string orgId, CancellationToken ct = default)
    {
        const string sql = "UPDATE secrets SET deleted_at = NOW(), updated_at = NOW() WHERE id = @Id AND org_id = @OrgId";
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        _logger.Information("Soft-deleted secret {SecretId}", id);
    }
}
