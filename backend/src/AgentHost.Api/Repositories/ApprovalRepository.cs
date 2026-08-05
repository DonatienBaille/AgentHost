using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

/// <summary>
/// approvals has no org_id column, so tenant scoping joins through the owning run
/// (approvals.run_id -&gt; runs.org_id).
/// </summary>
public interface IApprovalRepository
{
    Task<Approval?> GetAsync(string id, string orgId, CancellationToken ct = default);
    Task<List<Approval>> ListByRunAsync(string runId, string orgId, CancellationToken ct = default);

    /// <summary>
    /// Unscoped — used by the run lifecycle after the caller has already been authorized against
    /// the run itself, and by background workers that have no caller org.
    /// </summary>
    Task<Approval?> GetPendingForRunAsync(string runId, CancellationToken ct = default);
    Task InsertAsync(Approval approval, CancellationToken ct = default);
    Task UpdateAsync(Approval approval, CancellationToken ct = default);
}

public class ApprovalRepository : IApprovalRepository
{
    private const string SelectColumns = """
        id, run_id, step_id, approval_type, prompt, options,
        required_role, required_count, responses,
        status, expires_at, decided_at, decided_by, created_at
        """;

    private const string ScopedSelectColumns = """
        ap.id, ap.run_id, ap.step_id, ap.approval_type, ap.prompt, ap.options,
        ap.required_role, ap.required_count, ap.responses,
        ap.status, ap.expires_at, ap.decided_at, ap.decided_by, ap.created_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public ApprovalRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Approval?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {ScopedSelectColumns} FROM approvals ap
            JOIN runs r ON r.id = ap.run_id
            WHERE ap.id = @Id AND r.org_id = @OrgId AND r.deleted_at IS NULL
            """;
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Approval>(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
    }

    public async Task<List<Approval>> ListByRunAsync(string runId, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {ScopedSelectColumns} FROM approvals ap
            JOIN runs r ON r.id = ap.run_id
            WHERE ap.run_id = @RunId AND r.org_id = @OrgId AND r.deleted_at IS NULL
            ORDER BY ap.created_at ASC
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Approval>(new CommandDefinition(sql, new { RunId = runId, OrgId = orgId }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<Approval?> GetPendingForRunAsync(string runId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM approvals
            WHERE run_id = @RunId AND status = 'pending'
            ORDER BY created_at DESC
            LIMIT 1
            """;
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Approval>(new CommandDefinition(sql, new { RunId = runId }, cancellationToken: ct));
    }

    public async Task InsertAsync(Approval approval, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO approvals (
                id, run_id, step_id, approval_type, prompt, options,
                required_role, required_count, responses,
                status, expires_at, decided_at, decided_by, created_at
            ) VALUES (
                @Id, @RunId, @StepId, @ApprovalType, @Prompt, @Options::jsonb,
                @RequiredRole, @RequiredCount, @Responses::jsonb,
                @Status, @ExpiresAt, @DecidedAt, @DecidedBy, @CreatedAt
            )
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, approval, cancellationToken: ct));
        _logger.Information("Inserted approval {ApprovalId} for run {RunId}", approval.Id, approval.RunId);
    }

    public async Task UpdateAsync(Approval approval, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE approvals
            SET responses = @Responses::jsonb,
                status = @Status,
                decided_at = @DecidedAt,
                decided_by = @DecidedBy
            WHERE id = @Id
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, approval, cancellationToken: ct));
        _logger.Information("Updated approval {ApprovalId} to status {Status}", approval.Id, approval.Status);
    }
}
