using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IWebhookRepository
{
    Task<Webhook?> GetAsync(string id, CancellationToken ct = default);
    Task<List<Webhook>> ListByProjectAsync(string projectId, CancellationToken ct = default);
    Task<List<Webhook>> ListActiveForEventAsync(string projectId, string eventName, CancellationToken ct = default);
    Task InsertAsync(Webhook webhook, CancellationToken ct = default);
    Task UpdateAsync(Webhook webhook, CancellationToken ct = default);
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

    public async Task<Webhook?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM webhooks WHERE id = @Id";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Webhook>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<List<Webhook>> ListByProjectAsync(string projectId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM webhooks WHERE project_id = @ProjectId ORDER BY created_at DESC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Webhook>(new CommandDefinition(sql, new { ProjectId = projectId }, cancellationToken: ct));
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
}
