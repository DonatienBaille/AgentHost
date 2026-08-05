using Npgsql;
using Serilog;

namespace AgentHost.Api.Infrastructure;

/// <summary>
/// Lightweight, idempotent startup migration runner. Applies `../../../migrations/*.sql`
/// (relative to the API project's content root, i.e. the repo-root `migrations/` folder)
/// in filename order against the configured database, tracking applied filenames in a
/// `schema_migrations` table it creates if missing.
///
/// In the docker-compose deployment, Postgres already applies `migrations/*.sql` itself via
/// the `docker-entrypoint-initdb.d` mount, so the migrations folder generally won't be present
/// inside the backend container image — in that case this runner just logs and no-ops, letting
/// Postgres's own init have already done the work.
///
/// Every replica runs this at startup, so the whole read-then-apply sequence is serialized behind a
/// session-level Postgres advisory lock (<see cref="MigrationLockKey"/>). Without it two pods
/// starting together can both read `schema_migrations` before either writes, and both apply the
/// same file. The lock is taken on the same connection that does the work — the advisory lock is
/// session-scoped, so it would not protect anything held on a different session — and released in a
/// `finally`; the session ending also releases it, so a crashed pod cannot wedge the others.
/// </summary>
public class MigrationRunner
{
    /// <summary>
    /// Fixed, arbitrary key identifying "the AgentHost schema migration lock". Any value works as
    /// long as every replica uses the same one and nothing else in the database reuses it.
    /// </summary>
    private const long MigrationLockKey = 7_235_812_004_119_001L;

    /// <summary>
    /// Seconds a replica waits for a peer's migration run before giving up. Bounded so a wedged
    /// peer surfaces as a startup failure instead of an indefinite hang.
    /// </summary>
    private const int LockWaitSeconds = 300;

    private readonly string _connectionString;
    private readonly string _contentRootPath;
    private readonly ILogger _logger;

    public MigrationRunner(IConfiguration config, IHostEnvironment env, ILogger logger)
    {
        _connectionString = config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured");
        _contentRootPath = env.ContentRootPath;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var migrationsDir = ResolveMigrationsDirectory();
        if (migrationsDir is null)
        {
            _logger.Warning("Migrations directory not found near {ContentRoot}; skipping startup migrations " +
                             "(assuming the database was already initialized, e.g. via docker-entrypoint-initdb.d)",
                _contentRootPath);
            return;
        }

        var files = Directory.GetFiles(migrationsDir, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            _logger.Information("No .sql migration files found in {MigrationsDir}", migrationsDir);
            return;
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);

        await using (var lockCmd = connection.CreateCommand())
        {
            lockCmd.CommandText = "SELECT pg_advisory_lock(@key)";
            lockCmd.Parameters.AddWithValue("key", MigrationLockKey);
            lockCmd.CommandTimeout = LockWaitSeconds;
            await lockCmd.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await ApplyPendingAsync(connection, files, ct);
        }
        finally
        {
            try
            {
                await using var unlockCmd = connection.CreateCommand();
                unlockCmd.CommandText = "SELECT pg_advisory_unlock(@key)";
                unlockCmd.Parameters.AddWithValue("key", MigrationLockKey);
                // Not cancellable: releasing the lock must happen even when startup is aborting.
                await unlockCmd.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Closing the connection below releases the session lock anyway.
                _logger.Warning(ex, "Failed to release the migration advisory lock explicitly");
            }
        }
    }

    private async Task ApplyPendingAsync(NpgsqlConnection connection, List<string> files, CancellationToken ct)
    {
        await using (var createTableCmd = connection.CreateCommand())
        {
            createTableCmd.CommandText = """
                CREATE TABLE IF NOT EXISTS schema_migrations (
                    filename VARCHAR(255) PRIMARY KEY,
                    applied_at TIMESTAMP NOT NULL DEFAULT NOW()
                )
                """;
            await createTableCmd.ExecuteNonQueryAsync(ct);
        }

        var applied = new HashSet<string>();
        await using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT filename FROM schema_migrations";
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                applied.Add(reader.GetString(0));
        }

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            if (applied.Contains(filename))
                continue;

            var sql = await File.ReadAllTextAsync(file, ct);

            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = (NpgsqlTransaction)transaction;
                    cmd.CommandText = sql;
                    cmd.CommandTimeout = 120;
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await using (var markCmd = connection.CreateCommand())
                {
                    markCmd.Transaction = (NpgsqlTransaction)transaction;
                    markCmd.CommandText = "INSERT INTO schema_migrations (filename) VALUES (@filename)";
                    markCmd.Parameters.AddWithValue("filename", filename);
                    await markCmd.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                _logger.Information("Applied migration {Filename}", filename);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(ct);
                _logger.Error(ex, "Failed to apply migration {Filename}", filename);
                throw;
            }
        }
    }

    private string? ResolveMigrationsDirectory()
    {
        // Primary: spec-mandated relative path from the API project directory.
        var primary = Path.GetFullPath(Path.Combine(_contentRootPath, "..", "..", "..", "migrations"));
        if (Directory.Exists(primary) && Directory.GetFiles(primary, "*.sql").Length > 0)
            return primary;

        // Fallback: walk up from content root looking for a migrations/ folder (covers
        // running from a published/flattened output directory).
        var dir = new DirectoryInfo(_contentRootPath);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "migrations");
            if (Directory.Exists(candidate) && Directory.GetFiles(candidate, "*.sql").Length > 0)
                return candidate;
        }

        return null;
    }
}
