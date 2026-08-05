using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IRunRepository
{
    Task<Run?> GetAsync(string id, CancellationToken ct = default);
    Task<List<Run>> ListAsync(int skip, int take, CancellationToken ct = default);
    Task<List<Run>> ListByProjectAsync(string projectId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task<List<Run>> ListByOrgAsync(string orgId, int skip = 0, int take = 50, CancellationToken ct = default);
    Task InsertAsync(Run run, CancellationToken ct = default);
    Task UpdateAsync(Run run, CancellationToken ct = default);
    Task<long> GetNextRunNumberAsync(string projectId, CancellationToken ct = default);

    /// <summary>
    /// Atomically adds <paramref name="deltaUsd"/> to the run's <c>budget_used_usd</c> and returns
    /// the new total. Done as a single UPDATE ... RETURNING so concurrent usage reports from the
    /// same agent can never lose an increment the way read-modify-write would.
    /// </summary>
    Task<decimal?> AddBudgetUsageAsync(string runId, decimal deltaUsd, CancellationToken ct = default);

    /// <summary>Total budget consumed by a project's runs created at/after <paramref name="sinceUtc"/> (project monthly budget enforcement).</summary>
    Task<decimal> SumBudgetUsedForProjectSinceAsync(string projectId, DateTime sinceUtc, CancellationToken ct = default);

    /// <summary>Non-terminal runs that have actually started — the watchdog's timeout candidates.</summary>
    Task<List<Run>> ListActiveStartedAsync(CancellationToken ct = default);

    /// <summary>
    /// Writes only <c>runs.outputs</c>. Targeted rather than a full row update so an agent
    /// publishing its results cannot clobber a <c>budget_used_usd</c> increment written concurrently
    /// by its own usage reports.
    /// </summary>
    Task<bool> SetOutputsAsync(string runId, System.Text.Json.Nodes.JsonNode? outputs, CancellationToken ct = default);
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

    public async Task<List<Run>> ListAsync(int skip, int take, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM runs
            WHERE deleted_at IS NULL
            ORDER BY created_at DESC
            LIMIT @Take OFFSET @Skip
            """;
        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { Skip = skip, Take = take }, cancellationToken: ct);
        var runs = await db.QueryAsync<Run>(command);
        return runs.ToList();
    }

    public async Task<List<Run>> ListByProjectAsync(string projectId, int skip = 0, int take = 50, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM runs
            WHERE project_id = @ProjectId AND deleted_at IS NULL
            ORDER BY created_at DESC
            LIMIT @Take OFFSET @Skip
            """;
        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { ProjectId = projectId, Skip = skip, Take = take }, cancellationToken: ct);
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
        var command = new CommandDefinition(sql, new { OrgId = orgId, Skip = skip, Take = take }, cancellationToken: ct);
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

    public async Task<decimal?> AddBudgetUsageAsync(string runId, decimal deltaUsd, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE runs
            SET budget_used_usd = COALESCE(budget_used_usd, 0) + @Delta,
                updated_at = NOW()
            WHERE id = @Id AND deleted_at IS NULL
            RETURNING budget_used_usd
            """;

        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { Id = runId, Delta = deltaUsd }, cancellationToken: ct);
        return await db.ExecuteScalarAsync<decimal?>(command);
    }

    public async Task<decimal> SumBudgetUsedForProjectSinceAsync(string projectId, DateTime sinceUtc, CancellationToken ct = default)
    {
        const string sql = """
            SELECT COALESCE(SUM(COALESCE(budget_used_usd, 0)), 0)
            FROM runs
            WHERE project_id = @ProjectId AND created_at >= @Since AND deleted_at IS NULL
            """;

        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { ProjectId = projectId, Since = sinceUtc }, cancellationToken: ct);
        return await db.ExecuteScalarAsync<decimal>(command);
    }

    public async Task<bool> SetOutputsAsync(string runId, System.Text.Json.Nodes.JsonNode? outputs, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE runs
            SET outputs = @Outputs::jsonb,
                updated_at = NOW()
            WHERE id = @Id AND deleted_at IS NULL
            """;

        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(
            sql, new { Id = runId, Outputs = outputs?.ToJsonString() }, cancellationToken: ct);
        var rows = await db.ExecuteAsync(command);
        return rows > 0;
    }

    /// <summary>
    /// Candidate runs for the watchdog's timeout sweep.
    ///
    /// SQL narrows on the columns that are reliable (started, not deleted, started recently); the
    /// terminal-status filter is applied in C# because Dapper writes enum parameters as their
    /// underlying ordinal rather than through the registered TypeHandler, so `runs.status` does not
    /// hold the 'succeeded'/'failed'/... text a SQL predicate would need. The lookback keeps the
    /// candidate set bounded: no manifest maxDuration comes close to a week.
    /// </summary>
    public async Task<List<Run>> ListActiveStartedAsync(CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM runs
            WHERE deleted_at IS NULL
              AND started_at IS NOT NULL
              AND started_at > NOW() - INTERVAL '7 days'
            ORDER BY started_at ASC
            LIMIT 1000
            """;

        using var db = _connectionFactory.CreateConnection();
        var runs = await db.QueryAsync<Run>(new CommandDefinition(sql, cancellationToken: ct));
        return runs.Where(r => !r.Status.IsTerminal()).ToList();
    }
}
