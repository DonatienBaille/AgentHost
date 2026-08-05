using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

public interface IMemoryService
{
    Task<ProjectMemory> GetProjectMemoryAsync(string projectId, CancellationToken ct = default);
    Task UpdateProjectMemoryAsync(string projectId, MemoryUpdate update, CancellationToken ct = default);
    Task ArchiveOldRunsAsync(string projectId, CancellationToken ct = default);
}

/// <summary>Per-project "dream-like" agentic memory service (spec section 11.2, implemented verbatim).</summary>
public class MemoryService : IMemoryService
{
    /// <summary>
    /// Upper bound on retained <c>run_history</c> entries. Every terminal run transition now appends
    /// one (see <see cref="RunStateMachine"/>), so an unbounded list would grow with the project's
    /// entire run count inside a single jsonb column. Older entries beyond this window are dropped
    /// here; <see cref="ArchiveOldRunsAsync"/> independently rolls up anything older than 30 days.
    /// </summary>
    public const int MaxRunHistoryItems = 200;

    private readonly IMemoryRepository _memoryRepository;
    private readonly ILogger _logger;

    public MemoryService(IMemoryRepository memoryRepository, ILogger logger)
    {
        _memoryRepository = memoryRepository;
        _logger = logger;
    }

    public async Task<ProjectMemory> GetProjectMemoryAsync(string projectId, CancellationToken ct = default)
    {
        var memory = await _memoryRepository.GetAsync(projectId, ct);

        if (memory == null)
        {
            memory = new ProjectMemory
            {
                ProjectId = projectId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            };

            await _memoryRepository.InsertAsync(memory, ct);
        }

        return memory;
    }

    public async Task UpdateProjectMemoryAsync(
        string projectId,
        MemoryUpdate update,
        CancellationToken ct = default)
    {
        var memory = await GetProjectMemoryAsync(projectId, ct);

        if (update.ContextUpdates != null)
        {
            memory.Context = update.ContextUpdates;
        }

        if (update.NewLearnings?.Any() == true)
        {
            memory.Learnings.AddRange(update.NewLearnings);
        }

        if (update.NewNotes?.Any() == true)
        {
            memory.Notes.AddRange(update.NewNotes);
        }

        if (update.NewRunHistoryItem is not null)
        {
            memory.RunHistory.Add(update.NewRunHistoryItem);

            if (memory.RunHistory.Count > MaxRunHistoryItems)
                memory.RunHistory.RemoveRange(0, memory.RunHistory.Count - MaxRunHistoryItems);
        }

        memory.Patterns = DetectPatterns(memory.RunHistory);

        memory.UpdatedAt = DateTime.UtcNow;

        await _memoryRepository.UpdateAsync(projectId, memory, ct);

        _logger.Information("Updated memory for project {ProjectId}", projectId);
    }

    public async Task ArchiveOldRunsAsync(string projectId, CancellationToken ct = default)
    {
        var memory = await GetProjectMemoryAsync(projectId, ct);

        var cutoffDate = DateTime.UtcNow.AddDays(-30);
        var oldRuns = memory.RunHistory.Where(r => r.Timestamp < cutoffDate).ToList();

        if (oldRuns.Any())
        {
            var periods = memory.RunHistory
                .Where(r => r.Timestamp < cutoffDate)
                .GroupBy(r => r.Timestamp.ToString("yyyy-MM"))
                .Select(g => new ArchivePeriod
                {
                    Period = g.Key,
                    Summary = $"{g.Count()} runs processed",
                    CreatedAt = DateTime.UtcNow,
                })
                .ToList();

            memory.Archived.AddRange(periods);
            memory.RunHistory.RemoveAll(r => r.Timestamp < cutoffDate);

            await _memoryRepository.UpdateAsync(projectId, memory, ct);

            _logger.Information("Archived {Count} old runs for project {ProjectId}",
                oldRuns.Count, projectId);
        }
    }

    /// <summary>Scans the last 20 run history items for recurring patterns (spec 11.2).</summary>
    internal static List<Pattern> DetectPatterns(List<RunHistoryItem> history)
    {
        var patterns = new Dictionary<string, int>();

        foreach (var run in history.TakeLast(20))
        {
            if (run.Summary?.Contains("NullPointer") == true)
                IncrementPattern(patterns, "Null pointer exceptions");

            if (run.Summary?.Contains("error handling") == true)
                IncrementPattern(patterns, "Missing error handling");

            if (run.Outcome == "failed")
                IncrementPattern(patterns, "Failed runs");
        }

        return patterns
            .OrderByDescending(p => p.Value)
            .Select(p => new Pattern
            {
                Name = p.Key,
                Frequency = p.Value,
                LastSeen = DateTime.UtcNow,
            })
            .ToList();
    }

    private static void IncrementPattern(Dictionary<string, int> dict, string key)
    {
        if (dict.ContainsKey(key))
            dict[key]++;
        else
            dict[key] = 1;
    }
}
