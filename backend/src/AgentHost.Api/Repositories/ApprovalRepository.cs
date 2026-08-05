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

    /// <summary>Pending approvals whose <c>expires_at</c> has passed — the watchdog's expiry candidates.</summary>
    Task<List<Approval>> ListExpiredPendingAsync(DateTime nowUtc, CancellationToken ct = default);
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
        var row = await db.QueryFirstOrDefaultAsync<ApprovalRow>(new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct));
        return row?.ToDomain();
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
        var rows = await db.QueryAsync<ApprovalRow>(new CommandDefinition(sql, new { RunId = runId, OrgId = orgId }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <summary>
    /// The run's most recent still-pending approval. <c>approvals.status</c> holds the documented
    /// 'pending'/'approved'/... text (ApprovalStatusExtensions.ToDbString, backfilled by
    /// migrations/0004_enum_string_encoding.sql), so the filter belongs in SQL — before that
    /// migration the column held the enum's ordinal and this predicate would have matched nothing.
    /// </summary>
    public async Task<Approval?> GetPendingForRunAsync(string runId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM approvals
            WHERE run_id = @RunId AND status = @Pending
            ORDER BY created_at DESC
            """;
        using var db = _connectionFactory.CreateConnection();
        var row = await db.QueryFirstOrDefaultAsync<ApprovalRow>(new CommandDefinition(
            sql, new { RunId = runId, Pending = ApprovalStatus.Pending.ToDbString() }, cancellationToken: ct));
        return row?.ToDomain();
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
        await db.ExecuteAsync(new CommandDefinition(sql, ApprovalRow.FromDomain(approval), cancellationToken: ct));
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
        await db.ExecuteAsync(new CommandDefinition(sql, ApprovalRow.FromDomain(approval), cancellationToken: ct));
        _logger.Information("Updated approval {ApprovalId} to status {Status}", approval.Id, approval.Status);
    }

    /// <summary>Pending approvals past their expiry — deadline and status both filtered in SQL.</summary>
    public async Task<List<Approval>> ListExpiredPendingAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM approvals
            WHERE expires_at < @Now AND status = @Pending
            ORDER BY expires_at ASC
            LIMIT 500
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<ApprovalRow>(new CommandDefinition(
            sql, new { Now = nowUtc, Pending = ApprovalStatus.Pending.ToDbString() }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }
}
