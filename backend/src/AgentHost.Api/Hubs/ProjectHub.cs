using AgentHost.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace AgentHost.Api.Hubs;

/// <summary>Real-time hub for project-scoped sessions: memory + recent runs (spec section 10.2).</summary>
public class ProjectHub : Hub
{
    private readonly IMemoryService _memoryService;
    private readonly IRunService _runService;

    public ProjectHub(IMemoryService memoryService, IRunService runService)
    {
        _memoryService = memoryService;
        _runService = runService;
    }

    public async Task JoinProject(string projectId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"project-{projectId}");

        var memory = await _memoryService.GetProjectMemoryAsync(projectId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("projectMemory", memory);

        var recentRuns = await _runService.ListByProjectAsync(projectId, take: 10, ct: Context.ConnectionAborted);
        await Clients.Caller.SendAsync("recentRuns", recentRuns);
    }

    public async Task LeaveProject(string projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project-{projectId}");
    }
}
