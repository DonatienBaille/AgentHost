using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

public class MemoryUpdate
{
    public ProjectContext? ContextUpdates { get; set; }
    public List<Learning>? NewLearnings { get; set; }
    public List<Note>? NewNotes { get; set; }
    public RunHistoryItem? NewRunHistoryItem { get; set; }
}
