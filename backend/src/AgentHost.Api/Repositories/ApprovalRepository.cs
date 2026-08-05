using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IApprovalRepository
{
    Task<Approval?> GetAsync(string id, CancellationToken ct = default);
    Task<List<Approval>> ListByRunAsync(string runId, CancellationToken ct = default);
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

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public ApprovalRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Approval?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM approvals WHERE id = @Id";
        using var db = _connectionFactory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<Approval>(new CommandDefinition(sql, new { Id = id }, cancellationToken: ct));
    }

    public async Task<List<Approval>> ListByRunAsync(string runId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM approvals WHERE run_id = @RunId ORDER BY created_at ASC";
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Approval>(new CommandDefinition(sql, new { RunId = runId }, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>
    /// The run's most recent still-pending approval.
    ///
    /// The status filter is applied in C#, not SQL, on purpose. Dapper does not apply a registered
    /// enum TypeHandler when *writing* a parameter (it short-circuits enums to their underlying
    /// integer before consulting handlers), so `approvals.status` holds the enum's ordinal — not the
    /// 'pending'/'approved' text the column comment describes. A `status = 'pending'` predicate
    /// therefore silently matched nothing, which is why this method always returned null once
    /// approvals actually started being created. Reading through the mapped enum round-trips
    /// correctly whichever encoding a row was written with, so the comparison is done there.
    /// </summary>
    public async Task<Approval?> GetPendingForRunAsync(string runId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM approvals
            WHERE run_id = @RunId
            ORDER BY created_at DESC
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Approval>(new CommandDefinition(sql, new { RunId = runId }, cancellationToken: ct));
        return rows.FirstOrDefault(a => a.Status == ApprovalStatus.Pending);
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

    /// <summary>
    /// Pending approvals past their expiry. As in <see cref="GetPendingForRunAsync"/>, the deadline
    /// is filtered in SQL and the status in C#, because the persisted status encoding cannot be
    /// relied on in a predicate.
    /// </summary>
    public async Task<List<Approval>> ListExpiredPendingAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM approvals
            WHERE expires_at < @Now
            ORDER BY expires_at ASC
            LIMIT 500
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<Approval>(new CommandDefinition(sql, new { Now = nowUtc }, cancellationToken: ct));
        return rows.Where(a => a.Status == ApprovalStatus.Pending).ToList();
    }
}
