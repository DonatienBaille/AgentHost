using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// Fake, in-memory implementation of <see cref="IMemoryRepository"/> so MemoryService tests
/// run standalone without a database.
/// </summary>
public class FakeMemoryRepository : IMemoryRepository
{
    private readonly Dictionary<string, ProjectMemory> _store = new();

    public Task<ProjectMemory?> GetAsync(string projectId, CancellationToken ct = default) =>
        Task.FromResult(_store.TryGetValue(projectId, out var m) ? Clone(m) : null);

    public Task InsertAsync(ProjectMemory memory, CancellationToken ct = default)
    {
        _store[memory.ProjectId] = Clone(memory);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(string projectId, ProjectMemory memory, CancellationToken ct = default)
    {
        _store[projectId] = Clone(memory);
        return Task.CompletedTask;
    }

    private static ProjectMemory Clone(ProjectMemory m) => new()
    {
        ProjectId = m.ProjectId,
        Context = m.Context,
        RunHistory = new List<RunHistoryItem>(m.RunHistory),
        Patterns = new List<Pattern>(m.Patterns),
        Learnings = new List<Learning>(m.Learnings),
        Notes = new List<Note>(m.Notes),
        Archived = new List<ArchivePeriod>(m.Archived),
        CreatedAt = m.CreatedAt,
        UpdatedAt = m.UpdatedAt,
    };
}

public class MemoryServiceTests
{
    private static MemoryService CreateSut(out FakeMemoryRepository repo)
    {
        repo = new FakeMemoryRepository();
        return new MemoryService(repo, Serilog.Log.Logger);
    }

    [Fact]
    public async Task GetProjectMemoryAsync_CreatesMemoryWhenMissing()
    {
        var sut = CreateSut(out var repo);

        var memory = await sut.GetProjectMemoryAsync("proj1");

        Assert.Equal("proj1", memory.ProjectId);
        Assert.Empty(memory.RunHistory);
        Assert.NotNull(await repo.GetAsync("proj1"));
    }

    [Fact]
    public async Task GetProjectMemoryAsync_ReturnsExistingMemory_DoesNotOverwrite()
    {
        var sut = CreateSut(out _);

        await sut.GetProjectMemoryAsync("proj1");
        await sut.UpdateProjectMemoryAsync("proj1", new MemoryUpdate
        {
            NewNotes = new List<Note> { new() { Id = "n1", AgentName = "bot", Text = "hi", Timestamp = DateTime.UtcNow } },
        });

        var second = await sut.GetProjectMemoryAsync("proj1");

        Assert.Single(second.Notes);
    }

    [Fact]
    public async Task UpdateProjectMemoryAsync_AppendsLearningsAndNotes()
    {
        var sut = CreateSut(out _);
        await sut.GetProjectMemoryAsync("proj1");

        var update = new MemoryUpdate
        {
            NewLearnings = new List<Learning>
            {
                new() { Id = "l1", Title = "Use retries", Description = "desc", CreatedAt = DateTime.UtcNow },
            },
            NewNotes = new List<Note>
            {
                new() { Id = "n1", AgentName = "bot", Text = "note", Timestamp = DateTime.UtcNow },
            },
        };

        await sut.UpdateProjectMemoryAsync("proj1", update);
        var memory = await sut.GetProjectMemoryAsync("proj1");

        Assert.Single(memory.Learnings);
        Assert.Single(memory.Notes);
    }

    [Fact]
    public async Task UpdateProjectMemoryAsync_ReplacesContext()
    {
        var sut = CreateSut(out _);
        await sut.GetProjectMemoryAsync("proj1");

        var newContext = new ProjectContext { Name = "MyProject", Description = "desc", Technologies = new List<string> { "C#" } };
        await sut.UpdateProjectMemoryAsync("proj1", new MemoryUpdate { ContextUpdates = newContext });

        var memory = await sut.GetProjectMemoryAsync("proj1");
        Assert.Equal("MyProject", memory.Context.Name);
        Assert.Contains("C#", memory.Context.Technologies);
    }

    [Fact]
    public void DetectPatterns_CountsNullPointerExceptions()
    {
        var history = new List<RunHistoryItem>
        {
            new() { RunId = "r1", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "failed", Summary = "NullPointer at line 5" },
            new() { RunId = "r2", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "failed", Summary = "NullPointer at line 10" },
            new() { RunId = "r3", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "succeeded", Summary = "all good" },
        };

        var patterns = MemoryService.DetectPatterns(history);

        var npePattern = Assert.Single(patterns, p => p.Name == "Null pointer exceptions");
        Assert.Equal(2, npePattern.Frequency);
    }

    [Fact]
    public void DetectPatterns_CountsMissingErrorHandling()
    {
        var history = new List<RunHistoryItem>
        {
            new() { RunId = "r1", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "succeeded", Summary = "missing error handling in module X" },
        };

        var patterns = MemoryService.DetectPatterns(history);

        var pattern = Assert.Single(patterns, p => p.Name == "Missing error handling");
        Assert.Equal(1, pattern.Frequency);
    }

    [Fact]
    public void DetectPatterns_CountsFailedRuns()
    {
        var history = new List<RunHistoryItem>
        {
            new() { RunId = "r1", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "failed", Summary = "oops" },
            new() { RunId = "r2", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "failed", Summary = "oops again" },
            new() { RunId = "r3", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "succeeded", Summary = "fine" },
        };

        var patterns = MemoryService.DetectPatterns(history);

        var pattern = Assert.Single(patterns, p => p.Name == "Failed runs");
        Assert.Equal(2, pattern.Frequency);
    }

    [Fact]
    public void DetectPatterns_OnlyScansLast20Items()
    {
        var history = new List<RunHistoryItem>();
        for (var i = 0; i < 25; i++)
        {
            // 5 old failed runs (outside the last-20 window) + 20 recent succeeded runs.
            history.Add(new RunHistoryItem
            {
                RunId = $"r{i}",
                AgentName = "a",
                Timestamp = DateTime.UtcNow,
                Outcome = i < 5 ? "failed" : "succeeded",
                Summary = "n/a",
            });
        }

        var patterns = MemoryService.DetectPatterns(history);

        Assert.DoesNotContain(patterns, p => p.Name == "Failed runs");
    }

    [Fact]
    public void DetectPatterns_ReturnsEmptyForEmptyHistory()
    {
        var patterns = MemoryService.DetectPatterns(new List<RunHistoryItem>());
        Assert.Empty(patterns);
    }

    [Fact]
    public void DetectPatterns_OrdersByFrequencyDescending()
    {
        var history = new List<RunHistoryItem>
        {
            new() { RunId = "r1", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "failed", Summary = "x" },
            new() { RunId = "r2", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "failed", Summary = "NullPointer" },
            new() { RunId = "r3", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "failed", Summary = "NullPointer" },
        };

        var patterns = MemoryService.DetectPatterns(history);

        // "Failed runs" (3) should rank above "Null pointer exceptions" (2).
        Assert.Equal("Failed runs", patterns[0].Name);
        Assert.Equal(3, patterns[0].Frequency);
    }

    [Fact]
    public async Task ArchiveOldRunsAsync_MovesRunsOlderThan30DaysIntoArchivePeriods()
    {
        var sut = CreateSut(out _);
        await sut.GetProjectMemoryAsync("proj1");

        await sut.UpdateProjectMemoryAsync("proj1", new MemoryUpdate
        {
            NewRunHistoryItem = new RunHistoryItem { RunId = "old1", AgentName = "a", Timestamp = DateTime.UtcNow.AddDays(-40), Outcome = "succeeded", Summary = "old" },
        });
        await sut.UpdateProjectMemoryAsync("proj1", new MemoryUpdate
        {
            NewRunHistoryItem = new RunHistoryItem { RunId = "old2", AgentName = "a", Timestamp = DateTime.UtcNow.AddDays(-35), Outcome = "failed", Summary = "old" },
        });
        await sut.UpdateProjectMemoryAsync("proj1", new MemoryUpdate
        {
            NewRunHistoryItem = new RunHistoryItem { RunId = "recent1", AgentName = "a", Timestamp = DateTime.UtcNow.AddDays(-1), Outcome = "succeeded", Summary = "recent" },
        });

        await sut.ArchiveOldRunsAsync("proj1");

        var updated = await sut.GetProjectMemoryAsync("proj1");
        Assert.Single(updated.RunHistory);
        Assert.Equal("recent1", updated.RunHistory[0].RunId);
        Assert.NotEmpty(updated.Archived);
    }

    [Fact]
    public async Task ArchiveOldRunsAsync_NoOldRuns_DoesNothing()
    {
        var sut = CreateSut(out _);
        await sut.GetProjectMemoryAsync("proj1");
        await sut.UpdateProjectMemoryAsync("proj1", new MemoryUpdate
        {
            NewRunHistoryItem = new RunHistoryItem { RunId = "recent1", AgentName = "a", Timestamp = DateTime.UtcNow, Outcome = "succeeded", Summary = "recent" },
        });

        await sut.ArchiveOldRunsAsync("proj1");

        var updated = await sut.GetProjectMemoryAsync("proj1");
        Assert.Single(updated.RunHistory);
        Assert.Empty(updated.Archived);
    }
}
