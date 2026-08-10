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

    /// <summary>
    /// Écrit la ligne comme <see cref="UpdateAsync"/>, mais <b>seulement</b> si le statut en base
    /// est encore <paramref name="expectedStatus"/>. Rend faux quand un autre appelant a déjà fait
    /// changer l'état.
    ///
    /// C'est le garde-fou de la machine à états. Celle-ci vérifiait la validité d'une transition
    /// sur l'objet qu'elle avait en mémoire, puis écrivait sans condition : quatre approbations
    /// simultanées du même run lisaient toutes <c>awaiting_approval</c>, passaient toutes le
    /// contrôle, et reprenaient toutes le run — quatre lancements, quatre facturations. Le seul
    /// endroit où cette exclusion peut être décidée est la base, dans l'instruction d'écriture
    /// elle-même.
    /// </summary>
    Task<bool> TryUpdateWithExpectedStatusAsync(Run run, RunStatus expectedStatus, CancellationToken ct = default);
    Task<long> GetNextRunNumberAsync(string projectId, CancellationToken ct = default);

    /// <summary>
    /// Atomically adds <paramref name="deltaUsd"/> to the run's <c>budget_used_usd</c> and returns
    /// the new total. Done as a single UPDATE ... RETURNING so concurrent usage reports from the
    /// same agent can never lose an increment the way read-modify-write would.
    /// </summary>
    Task<decimal?> AddBudgetUsageAsync(string runId, decimal deltaUsd, CancellationToken ct = default);

    /// <summary>Total budget consumed by a project's runs created at/after <paramref name="sinceUtc"/> (project monthly budget enforcement).</summary>
    Task<decimal> SumBudgetUsedForProjectSinceAsync(string projectId, DateTime sinceUtc, CancellationToken ct = default);

    /// <summary>
    /// L'arbre de chaînage complet auquel appartient <paramref name="rootRunId"/>, scopé à
    /// l'organisation (feuille de route, lot 4).
    ///
    /// Une seule condition indexée et aucune CTE récursive : <c>root_run_id</c> est porté par TOUS
    /// les descendants, pas seulement par les enfants directs, si bien que l'arbre entier tient
    /// dans <c>root_run_id = @Root OR id = @Root</c>. C'est précisément ce que cette colonne
    /// existe pour rendre possible.
    /// </summary>
    Task<List<Run>> GetChainTreeAsync(string rootRunId, string orgId, CancellationToken ct = default);

    /// <summary>Nombre d'enfants directs d'un run — borne l'éventail d'un seul parent.</summary>
    Task<int> CountChildrenAsync(string parentRunId, CancellationToken ct = default);

    /// <summary>Nombre total de runs dans l'arbre — borne la taille d'une cascade entière.</summary>
    Task<int> CountChainTreeAsync(string rootRunId, CancellationToken ct = default);

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
        triggered_by_user_id, triggered_by_type, parent_run_id, root_run_id, chain_depth,
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
    /// Le prochain <c>number</c> d'un projet, alloué par le compteur porté par la ligne du projet
    /// (migration 0014).
    ///
    /// <b>Ce que remplace cette implémentation, et pourquoi.</b> La version précédente prenait un
    /// verrou consultatif autour d'un <c>SELECT MAX(number) + 1</c> puis le relâchait avant de
    /// rendre la valeur — donc AVANT que l'appelant n'insère. Le verrou protégeait la lecture, qui
    /// n'en avait pas besoin, et laissait sans protection l'intervalle entre la lecture et
    /// l'écriture, qui est le seul endroit où la course se produit. Douze créations simultanées
    /// sur un même projet en refusaient cinq, avec une violation de <c>UNIQUE (project_id,
    /// number)</c> remontée à l'appelant en erreur 500.
    ///
    /// L'<c>UPDATE ... RETURNING</c> ci-dessous n'a pas d'intervalle : le verrou de ligne que
    /// Postgres pose de lui-même sérialise les concurrents, et la valeur rendue est celle qui vient
    /// d'être écrite. Aucun autre projet n'est ralenti — le verrou porte sur une ligne.
    ///
    /// Un numéro alloué puis perdu (insertion refusée ensuite) laisse un trou. C'est le
    /// comportement de toute séquence, et il est préférable au précédent : un trou se remarque, un
    /// doublon corrompt.
    /// </summary>
    public async Task<long> GetNextRunNumberAsync(string projectId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE projects SET run_counter = run_counter + 1
            WHERE id = @ProjectId
            RETURNING run_counter
            """;

        using var db = _connectionFactory.CreateConnection();
        var next = await db.ExecuteScalarAsync<long?>(new CommandDefinition(
            sql, new { ProjectId = projectId }, cancellationToken: ct));

        // Aucune ligne mise à jour : le projet n'existe pas. Rendre 0 silencieusement produirait un
        // run rattaché à rien, que la clé étrangère refuserait ensuite avec un message obscur.
        return next ?? throw new InvalidOperationException(
            $"Impossible d'allouer un numéro de run : le projet {projectId} n'existe pas.");
    }

    public async Task InsertAsync(Run run, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO runs (
                id, org_id, project_id, number, agent_id, agent_version_id,
                status, inputs, context, outputs, workspace_path, runner_url,
                duration_ms, exit_code, error_message, error_code,
                budget_max_usd, budget_used_usd,
                triggered_by_user_id, triggered_by_type, parent_run_id, root_run_id, chain_depth,
                created_at, started_at, finished_at, updated_at
            ) VALUES (
                @Id, @OrgId, @ProjectId, @Number, @AgentId, @AgentVersionId,
                @Status, @Inputs::jsonb, @Context::jsonb, @Outputs::jsonb, @WorkspacePath, @RunnerUrl,
                @DurationMs, @ExitCode, @ErrorMessage, @ErrorCode,
                @BudgetMaxUsd, @BudgetUsedUsd,
                @TriggeredByUserId, @TriggeredByType, @ParentRunId, @RootRunId, @ChainDepth,
                @CreatedAt, @StartedAt, @FinishedAt, @UpdatedAt
            )
            """;

        using var db = _connectionFactory.CreateConnection();
        await db.ExecuteAsync(new CommandDefinition(sql, RunRow.FromDomain(run), cancellationToken: ct));
        _logger.Information("Inserted run {RunId}", run.Id);
    }

    public async Task<List<Run>> GetChainTreeAsync(string rootRunId, string orgId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT {SelectColumns} FROM runs
            WHERE org_id = @OrgId AND deleted_at IS NULL
              AND (root_run_id = @Root OR id = @Root)
            ORDER BY created_at
            """;
        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<RunRow>(new CommandDefinition(
            sql, new { Root = rootRunId, OrgId = orgId }, cancellationToken: ct));
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<int> CountChildrenAsync(string parentRunId, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM runs WHERE parent_run_id = @Parent AND deleted_at IS NULL",
            new { Parent = parentRunId }, cancellationToken: ct));
    }

    public async Task<int> CountChainTreeAsync(string rootRunId, CancellationToken ct = default)
    {
        using var db = _connectionFactory.CreateConnection();
        return await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM runs WHERE (root_run_id = @Root OR id = @Root) AND deleted_at IS NULL",
            new { Root = rootRunId }, cancellationToken: ct));
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

    public async Task<bool> TryUpdateWithExpectedStatusAsync(
        Run run, RunStatus expectedStatus, CancellationToken ct = default)
    {
        // Identique à UpdateAsync, à la condition près : c'est elle qui fait tout le travail.
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
            WHERE id = @Id AND status = @ExpectedStatus
            """;

        var parameters = new DynamicParameters(RunRow.FromDomain(run));
        parameters.Add("ExpectedStatus", expectedStatus.ToDbString());

        using var db = _connectionFactory.CreateConnection();
        var affected = await db.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));

        if (affected == 0)
        {
            _logger.Debug("Run {RunId}: transition {From} -> {To} refusée, l'état en base a changé",
                run.Id, expectedStatus, run.Status);
            return false;
        }

        _logger.Information("Updated run {RunId} status to {Status}", run.Id, run.Status);
        return true;
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
