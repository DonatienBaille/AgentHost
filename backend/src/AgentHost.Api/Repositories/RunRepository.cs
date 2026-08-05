using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IRunRepository
{
    /// <summary>
    /// Unscoped lookup — for background/system callers only (the run executor has no HTTP caller
    /// and therefore no org to scope by). Request handlers must use the
    /// <see cref="GetAsync(string, string, CancellationToken)"/> overload so tenant isolation is
    /// enforced by the SQL rather than by a check someone can forget to write.
    /// </summary>
    Task<Run?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Org-scoped lookup: returns null (=&gt; 404, never 403) for another tenant's run.</summary>
    Task<Run?> GetAsync(string id, string orgId, CancellationToken ct = default);

    Task<List<Run>> ListByProjectAsync(string projectId, string orgId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<List<Run>> ListByOrgAsync(string orgId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task InsertAsync(Run run, CancellationToken ct = default);
    Task UpdateAsync(Run run, CancellationToken ct = default);
    Task<long> GetNextRunNumberAsync(string projectId, CancellationToken ct = default);
}

public class RunRepository : IRunRepository
{
    private const string SelectColumns = """
        id, org_id, project_id, number, agent_id, agent_version_id,
        status, inputs, context, outputs, workspace_path,
        duration_ms, exit_code, error_message, error_code,
        budget_max_usd, budget_used_usd,
        triggered_by_user_id, triggered_by_type, parent_run_id, root_run_id,
        created_at, started_at, finished_at, updated_at, deleted_at
        """;

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public RunRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<Run?> GetAsync(string id, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM runs WHERE id = @Id AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { Id = id }, cancellationToken: ct);
        return await db.QueryFirstOrDefaultAsync<Run>(command);
    }

    public async Task<Run?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM runs WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct);
        return await db.QueryFirstOrDefaultAsync<Run>(command);
    }

    public async Task<List<Run>> ListByProjectAsync(string projectId, string orgId, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM runs
            WHERE project_id = @ProjectId AND org_id = @OrgId AND deleted_at IS NULL
            ORDER BY created_at DESC
            LIMIT @Take OFFSET @Skip
            """;
        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            sql,
            new { ProjectId = projectId, OrgId = orgId, Skip = Paging.ClampSkip(skip), Take = Paging.ClampTake(take) },
            cancellationToken: ct);
        var runs = await db.QueryAsync<Run>(command);
        return runs.ToList();
    }

    public async Task<List<Run>> ListByOrgAsync(string orgId, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM runs
            WHERE org_id = @OrgId AND deleted_at IS NULL
            ORDER BY created_at DESC
            LIMIT @Take OFFSET @Skip
            """;
        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            sql,
            new { OrgId = orgId, Skip = Paging.ClampSkip(skip), Take = Paging.ClampTake(take) },
            cancellationToken: ct);
        var runs = await db.QueryAsync<Run>(command);
        return runs.ToList();
    }

    /// <summary>
    /// Returns the next sequential `number` for a project. Uses a Postgres advisory lock keyed on
    /// the project id so concurrent run creations for the same project serialize on this step
    /// without needing a full table lock; unrelated projects are never blocked. The lock is
    /// session-scoped and released automatically when the connection returned to the pool closes
    /// (we open/close a dedicated connection per call here to bound the lock's lifetime tightly).
    /// </summary>
    public async Task<long> GetNextRunNumberAsync(string projectId, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        var lockKey = HashProjectId(projectId);

        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@Key)", new { Key = lockKey }, cancellationToken: ct));
        try
        {
            var next = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COALESCE(MAX(number), 0) + 1 FROM runs WHERE project_id = @ProjectId",
                new { ProjectId = projectId },
                cancellationToken: ct));
            return next;
        }
        finally
        {
            await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(@Key)", new { Key = lockKey }, cancellationToken: ct));
        }
    }

    private static long HashProjectId(string projectId)
    {
        // Deterministic 63-bit key for pg_advisory_lock(bigint) derived from the project ULID.
        unchecked
        {
            long hash = 17;
            foreach (var c in projectId)
                hash = hash * 31 + c;
            return hash & long.MaxValue;
        }
    }

    public async Task InsertAsync(Run run, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO runs (
                id, org_id, project_id, number, agent_id, agent_version_id,
                status, inputs, context, outputs, workspace_path,
                duration_ms, exit_code, error_message, error_code,
                budget_max_usd, budget_used_usd,
                triggered_by_user_id, triggered_by_type, parent_run_id, root_run_id,
                created_at, started_at, finished_at, updated_at
            ) VALUES (
                @Id, @OrgId, @ProjectId, @Number, @AgentId, @AgentVersionId,
                @Status, @Inputs::jsonb, @Context::jsonb, @Outputs::jsonb, @WorkspacePath,
                @DurationMs, @ExitCode, @ErrorMessage, @ErrorCode,
                @BudgetMaxUsd, @BudgetUsedUsd,
                @TriggeredByUserId, @TriggeredByType, @ParentRunId, @RootRunId,
                @CreatedAt, @StartedAt, @FinishedAt, @UpdatedAt
            )
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, run, cancellationToken: ct));
        _logger.Information("Inserted run {RunId}", run.Id);
    }

    public async Task UpdateAsync(Run run, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE runs
            SET status = @Status,
                inputs = @Inputs::jsonb,
                context = @Context::jsonb,
                outputs = @Outputs::jsonb,
                workspace_path = @WorkspacePath,
                duration_ms = @DurationMs,
                exit_code = @ExitCode,
                error_message = @ErrorMessage,
                error_code = @ErrorCode,
                budget_max_usd = @BudgetMaxUsd,
                budget_used_usd = @BudgetUsedUsd,
                started_at = @StartedAt,
                finished_at = @FinishedAt,
                updated_at = @UpdatedAt
            WHERE id = @Id
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, run, cancellationToken: ct));
        _logger.Information("Updated run {RunId} status to {Status}", run.Id, run.Status);
    }
}
