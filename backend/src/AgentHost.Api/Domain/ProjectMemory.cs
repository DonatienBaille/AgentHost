namespace AgentHost.Api.Domain;

/// <summary>Per-project shared "dream-like" memory (spec section 11.1).</summary>
public class ProjectMemory
{
    public string ProjectId { get; set; } = string.Empty;

    public ProjectContext Context { get; set; } = new();

    public List<RunHistoryItem> RunHistory { get; set; } = new();
    public List<Pattern> Patterns { get; set; } = new();
    public List<Learning> Learnings { get; set; } = new();
    public List<Note> Notes { get; set; } = new();
    public List<ArchivePeriod> Archived { get; set; } = new();

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class ProjectContext
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Technologies { get; set; } = new();
    public List<string> RecentDecisions { get; set; } = new();
}

public class RunHistoryItem
{
    public string RunId { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Outcome { get; set; } = string.Empty; // succeeded, failed, cancelled
    public string Summary { get; set; } = string.Empty;
}

public class Pattern
{
    public string Name { get; set; } = string.Empty;
    public int Frequency { get; set; }
    public DateTime LastSeen { get; set; }
}

public class Learning
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<string> AppliedByAgents { get; set; } = new();
}

public class Note
{
    public string Id { get; set; } = string.Empty;
    public string AgentName { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public class ArchivePeriod
{
    public string Period { get; set; } = string.Empty; // "2026-08"
    public string Summary { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
