using Dapper;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using Serilog;

namespace AgentHost.Api.Repositories;

public interface IRunEventRepository
{
    /// <summary>Inserts the event, atomically assigning the next `seq` for its run.</summary>
    Task<long> InsertAsync(RunEvent evt, CancellationToken ct = default);

    /// <summary>
    /// Unscoped listing for callers that have already authorized access to the run. Always
    /// bounded — an unbounded event fetch on a chatty run is a memory hazard even internally.
    /// </summary>
    Task<List<RunEvent>> ListByRunAsync(string runId, long fromSeq = 0, CancellationToken ct = default);

    /// <summary>
    /// Org-scoped listing (run_events has no org_id; joins through run_events.run_id -&gt;
    /// runs.org_id). Returns an empty list for another tenant's run.
    /// </summary>
    Task<List<RunEvent>> ListByRunAsync(string runId, string orgId, long fromSeq, int take, CancellationToken ct = default);
}

public class RunEventRepository : IRunEventRepository
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public RunEventRepository(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<long> InsertAsync(RunEvent evt, CancellationToken ct = default)
    {
        // seq is assigned atomically per run using the same advisory-lock pattern as run numbers,
        // avoiding gaps/races when multiple writers publish events for the same run concurrently.
        using var db = _connectionFactory.CreateConnection();
        var lockKey = HashRunId(evt.RunId);

        await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_lock(@Key)", new { Key = lockKey }, cancellationToken: ct));
        try
        {
            var nextSeq = await db.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COALESCE(MAX(seq), 0) + 1 FROM run_events WHERE run_id = @RunId",
                new { RunId = evt.RunId },
                cancellationToken: ct));

            evt.Seq = nextSeq;

            const string sql = """
                INSERT INTO run_events (run_id, seq, timestamp, event_type, level, message, payload)
                VALUES (@RunId, @Seq, @Timestamp, @EventType, @Level, @Message, @PayloadJson::jsonb)
                """;

            var payloadJson = evt.Payload is null
                ? null
                : System.Text.Json.JsonSerializer.Serialize(evt.Payload);

            await db.ExecuteAsync(new CommandDefinition(sql, new
            {
                evt.RunId,
                evt.Seq,
                evt.Timestamp,
                evt.EventType,
                evt.Level,
                evt.Message,
                PayloadJson = payloadJson,
            }, cancellationToken: ct));

            return nextSeq;
        }
        finally
        {
            await db.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(@Key)", new { Key = lockKey }, cancellationToken: ct));
        }
    }

    public async Task<List<RunEvent>> ListByRunAsync(string runId, long fromSeq = 0, CancellationToken ct = default)
    {
        const string sql = """
            SELECT run_id, seq, timestamp, event_type, level, message, payload
            FROM run_events
            WHERE run_id = @RunId AND seq >= @FromSeq
            ORDER BY seq ASC
            LIMIT @Take
            """;

        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<RunEventRow>(new CommandDefinition(
            sql, new { RunId = runId, FromSeq = fromSeq, Take = Paging.MaxTake }, cancellationToken: ct));
        return Materialize(rows);
    }

    public async Task<List<RunEvent>> ListByRunAsync(string runId, string orgId, long fromSeq, int take, CancellationToken ct = default)
    {
        const string sql = """
            SELECT e.run_id, e.seq, e.timestamp, e.event_type, e.level, e.message, e.payload
            FROM run_events e
            JOIN runs r ON r.id = e.run_id
            WHERE e.run_id = @RunId AND r.org_id = @OrgId AND r.deleted_at IS NULL AND e.seq >= @FromSeq
            ORDER BY e.seq ASC
            LIMIT @Take
            """;

        using var db = _connectionFactory.CreateConnection();
        var rows = await db.QueryAsync<RunEventRow>(new CommandDefinition(
            sql,
            new { RunId = runId, OrgId = orgId, FromSeq = fromSeq < 0 ? 0 : fromSeq, Take = Paging.ClampTake(take) },
            cancellationToken: ct));
        return Materialize(rows);
    }

    private static List<RunEvent> Materialize(IEnumerable<RunEventRow> rows) =>
        rows.Select(r => new RunEvent
        {
            RunId = r.RunId,
            Seq = r.Seq,
            Timestamp = r.Timestamp,
            EventType = r.EventType,
            Level = r.Level,
            Message = r.Message,
            Payload = string.IsNullOrWhiteSpace(r.Payload)
                ? null
                : System.Text.Json.Nodes.JsonNode.Parse(r.Payload),
        }).ToList();

    private static long HashRunId(string runId)
    {
        unchecked
        {
            long hash = 17;
            foreach (var c in runId)
                hash = hash * 31 + c;
            return hash & long.MaxValue;
        }
    }

    private class RunEventRow
    {
        public string RunId { get; set; } = string.Empty;
        public long Seq { get; set; }
        public DateTime Timestamp { get; set; }
        public string EventType { get; set; } = string.Empty;
        public string Level { get; set; } = string.Empty;
        public string? Message { get; set; }
        public string? Payload { get; set; }
    }
}
