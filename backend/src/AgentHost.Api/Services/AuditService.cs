using System.Text.Json.Nodes;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

public interface IAuditService
{
    Task RecordAsync(
        string orgId,
        string action,
        string? actorUserId = null,
        string? resourceType = null,
        string? resourceId = null,
        JsonNode? changes = null,
        JsonNode? details = null,
        CancellationToken ct = default);

    Task<List<AuditLogEntry>> ListByOrgAsync(string orgId, int skip = 0, int take = 100, CancellationToken ct = default);
}

/// <summary>Write-once, read-many audit trail service (spec section 2.1 "Audit WORM (non-suppressible)").</summary>
public class AuditService : IAuditService
{
    private readonly IAuditLogRepository _repository;
    private readonly ILogger _logger;

    public AuditService(IAuditLogRepository repository, ILogger logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public async Task RecordAsync(
        string orgId,
        string action,
        string? actorUserId = null,
        string? resourceType = null,
        string? resourceId = null,
        JsonNode? changes = null,
        JsonNode? details = null,
        CancellationToken ct = default)
    {
        var entry = new AuditLogEntry
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = orgId,
            Action = action,
            ActorUserId = actorUserId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Changes = changes,
            Details = details,
            CreatedAt = DateTime.UtcNow,
        };

        await _repository.InsertAsync(entry, ct);
    }

    public Task<List<AuditLogEntry>> ListByOrgAsync(string orgId, int skip = 0, int take = 100, CancellationToken ct = default) =>
        _repository.ListByOrgAsync(orgId, skip, take, ct);
}
