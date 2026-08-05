using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>
/// webhooks has no org_id column, so tenant scoping joins through the owning project
/// (webhooks.project_id -&gt; projects.org_id).
/// </summary>
public interface IWebhookRepository
{
    Task<Webhook?> GetAsync(string id, string orgId, CancellationToken ct = default);
    Task<List<Webhook>> ListByProjectAsync(string projectId, string orgId, CancellationToken ct = default);

    /// <summary>Unscoped — dispatch runs in the background for an already-authorized run's project.</summary>
    Task<List<Webhook>> ListActiveForEventAsync(string projectId, string eventName, CancellationToken ct = default);

    Task InsertAsync(Webhook webhook, CancellationToken ct = default);
    Task UpdateAsync(Webhook webhook, CancellationToken ct = default);
    Task DeleteAsync(string id, string orgId, CancellationToken ct = default);
}

public class WebhookRepository : IWebhookRepository
{
    private const string SelectColumns = """
        id, project_id, url, events, secret_token, is_active, created_at, updated_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public WebhookRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    private const string ScopedSelectColumns = """
        w.id, w.project_id, w.url, w.events, w.secret_token, w.is_active, w.created_at, w.updated_at
        """;

    public async Task<Webhook?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {ScopedSelectColumns} FROM webhooks w
            JOIN projects p ON p.id = w.project_id
            WHERE w.id = @Id AND p.org_id = @OrgId AND p.deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Webhook>(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
    }

    public async Task<List<Webhook>> ListByProjectAsync(string projectId, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {ScopedSelectColumns} FROM webhooks w
            JOIN projects p ON p.id = w.project_id
            WHERE w.project_id = @ProjectId AND p.org_id = @OrgId AND p.deleted_at IS NULL
            ORDER BY w.created_at DESC
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Webhook>(new CommandDefinition(sql, new { ProjectId = projectId, OrgId = orgId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<List<Webhook>> ListActiveForEventAsync(string projectId, string eventName, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM webhooks
            WHERE project_id = @ProjectId AND is_active = TRUE AND events @> to_jsonb(@EventName::text)
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Webhook>(new CommandDefinition(sql, new { ProjectId = projectId, EventName = eventName }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task InsertAsync(Webhook webhook, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO webhooks (id, project_id, url, events, secret_token, is_active, created_at, updated_at)
            VALUES (@Id, @ProjectId, @Url, @Events::jsonb, @SecretToken, @IsActive, @CreatedAt, @UpdatedAt)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, webhook, cancellationToken: ct));
        _logger.Information("Inserted webhook {WebhookId} for project {ProjectId}", webhook.Id, webhook.ProjectId);
    }

    public async Task UpdateAsync(Webhook webhook, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE webhooks
            SET url = @Url, events = @Events::jsonb, secret_token = @SecretToken, is_active = @IsActive, updated_at = @UpdatedAt
            WHERE id = @Id
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, webhook, cancellationToken: ct));
        _logger.Information("Updated webhook {WebhookId}", webhook.Id);
    }

    public async Task DeleteAsync(string id, string orgId, CancellationToken ct = default)
    {
        const string sql = """
            DELETE FROM webhooks
            WHERE id = @Id AND project_id IN (SELECT id FROM projects WHERE org_id = @OrgId)
            """;
        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        _logger.Information("Deleted webhook {WebhookId}", id);
    }
}
