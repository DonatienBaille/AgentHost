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

    /// <summary>
    /// Enregistre quel runner détient ce run (migration 0009). UPDATE ciblé, comme
    /// <see cref="SetOutputsAsync"/> : il est écrit depuis le chemin de lancement, en parallèle des
    /// transitions d'état, et n'a aucune raison d'écraser le reste de la ligne.
    /// </summary>
    Task<bool> SetRunnerUrlAsync(string runId, string? runnerUrl, CancellationToken ct = default);

    /// <summary>
    /// L'URL du runner détenant ce run, ou <c>null</c> quand aucun n'est enregistré. Lecture scalaire
    /// plutôt que chargement complet : c'est la question posée sur le chemin d'annulation et de
    /// lecture des journaux, et la seule dont la réponse serve à router.
    /// </summary>
    Task<string?> GetRunnerUrlAsync(string runId, CancellationToken ct = default);
}

public class RunRepository : IRunRepository
{
    private const string SelectColumns = """
        id, org_id, project_id, number, agent_id, agent_version_id,
        status, inputs, context, outputs, workspace_path, runner_url,
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
        var row = await db.QueryFirstOrDefaultAsync<RunRow>(command);
        return row?.ToDomain();
    }

    public async Task<Run?> GetAsync(string id, string orgId, CancellationToken ct = default)
    {
        var sql = $"SELECT {SelectColumns} FROM runs WHERE id = @Id AND org_id = @OrgId AND deleted_at IS NULL";
        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { Id = id, OrgId = orgId }, cancellationToken: ct);
        var row = await db.QueryFirstOrDefaultAsync<RunRow>(command);
        return row?.ToDomain();
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
        var rows = await db.QueryAsync<RunRow>(command);
        return rows.Select(r => r.ToDomain()).ToList();
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
        var rows = await db.QueryAsync<RunRow>(command);
        return rows.Select(r => r.ToDomain()).ToList();
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
                status, inputs, context, outputs, workspace_path, runner_url,
                duration_ms, exit_code, error_message, error_code,
                budget_max_usd, budget_used_usd,
                triggered_by_user_id, triggered_by_type, parent_run_id, root_run_id,
                created_at, started_at, finished_at, updated_at
            ) VALUES (
                @Id, @OrgId, @ProjectId, @Number, @AgentId, @AgentVersionId,
                @Status, @Inputs::jsonb, @Context::jsonb, @Outputs::jsonb, @WorkspacePath, @RunnerUrl,
                @DurationMs, @ExitCode, @ErrorMessage, @ErrorCode,
                @BudgetMaxUsd, @BudgetUsedUsd,
                @TriggeredByUserId, @TriggeredByType, @ParentRunId, @RootRunId,
                @CreatedAt, @StartedAt, @FinishedAt, @UpdatedAt
            )
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, RunRow.FromDomain(run), cancellationToken: ct));
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
        await db.ExecuteAsync(new CommandDefinition(sql, RunRow.FromDomain(run), cancellationToken: ct));
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

    public async Task<bool> SetRunnerUrlAsync(string runId, string? runnerUrl, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE runs
            SET runner_url = @RunnerUrl,
                updated_at = NOW()
            WHERE id = @Id AND deleted_at IS NULL
            """;

        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { Id = runId, RunnerUrl = runnerUrl }, cancellationToken: ct);
        return await db.ExecuteAsync(command) > 0;
    }

    public async Task<string?> GetRunnerUrlAsync(string runId, CancellationToken ct = default)
    {
        const string sql = "SELECT runner_url FROM runs WHERE id = @Id AND deleted_at IS NULL";

        using var db = _connectionFactory.CreateConnection();
        var command = new CommandDefinition(sql, new { Id = runId }, cancellationToken: ct);
        return await db.ExecuteScalarAsync<string?>(command);
    }

    /// <summary>
    /// Candidate runs for the watchdog's timeout sweep. The terminal-status filter runs in SQL:
    /// `runs.status` holds the documented snake_case text (see RunStatusExtensions.ToDbString and
    /// migrations/0004_enum_string_encoding.sql), so the predicate matches the same set
    /// <see cref="RunStatusExtensions.IsTerminal"/> would. The 7-day lookback keeps the candidate
    /// set bounded: no manifest maxDuration comes close to a week.
    /// </summary>
    public async Task<List<Run>> ListActiveStartedAsync(CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM runs
            WHERE deleted_at IS NULL
              AND started_at IS NOT NULL
              AND started_at > NOW() - INTERVAL '7 days'
              AND status <> ALL(@TerminalStatuses)
            ORDER BY started_at ASC
            LIMIT 1000
            """;

        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<RunRow>(new CommandDefinition(
            sql, new { TerminalStatuses = TerminalStatuses }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <summary>The DB encoding of every terminal <see cref="RunStatus"/>, derived from the enum itself.</summary>
    private static readonly string[] TerminalStatuses = Enum.GetValues<RunStatus>()
        .Where(s => s.IsTerminal())
        .Select(s => s.ToDbString())
        .ToArray();
}
