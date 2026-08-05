using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>Write-once, read-many audit trail (audit_log table). No update/delete by design.</summary>
public interface IAuditLogRepository
{
    Task InsertAsync(AuditLogEntry entry, CancellationToken ct = default);
    Task<List<AuditLogEntry>> ListByOrgAsync(string orgId, int skip = 0, int take = 100, CancellationToken ct = default);
    Task<List<AuditLogEntry>> ListByResourceAsync(string orgId, string resourceType, string resourceId, CancellationToken ct = default);
}

public class AuditLogRepository : IAuditLogRepository
{
    private const string SelectColumns = """
        id, org_id, action, actor_user_id, resource_type, resource_id,
        changes, details, created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public AuditLogRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task InsertAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO audit_log (id, org_id, action, actor_user_id, resource_type, resource_id, changes, details, created_at)
            VALUES (@Id, @OrgId, @Action, @ActorUserId, @ResourceType, @ResourceId, @Changes::jsonb, @Details::jsonb, @CreatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, entry, cancellationToken: ct));
        _logger.Information("Audit log: {Action} on {ResourceType} {ResourceId} by {ActorUserId}",
            entry.Action, entry.ResourceType, entry.ResourceId, entry.ActorUserId);
    }

    public async Task<List<AuditLogEntry>> ListByOrgAsync(string orgId, int skip = 0, int take = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM audit_log
            WHERE org_id = @OrgId
            ORDER BY created_at DESC
            LIMIT @Take OFFSET @Skip
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<AuditLogEntry>(new CommandDefinition(
            sql,
            new { OrgId = orgId, Skip = Paging.ClampSkip(skip), Take = Paging.ClampTake(take) },
            cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<AuditLogEntry>> ListByResourceAsync(string orgId, string resourceType, string resourceId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM audit_log
            WHERE org_id = @OrgId AND resource_type = @ResourceType AND resource_id = @ResourceId
            ORDER BY created_at DESC
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<AuditLogEntry>(new CommandDefinition(sql, new { OrgId = orgId, ResourceType = resourceType, ResourceId = resourceId }, cancellationToken: ct));
        return rows.ToList();
    }
}
