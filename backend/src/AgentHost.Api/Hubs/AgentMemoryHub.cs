using AgentHost.Api.Contracts;
using AgentHost.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace AgentHost.Api.Hubs;

/// <summary>Real-time hub for collaborative editing of a project's agentic memory (spec section 10.3).</summary>
public class AgentMemoryHub : Hub
{
    private readonly IMemoryService _memoryService;

    public AgentMemoryHub(IMemoryService memoryService)
    {
        _memoryService = memoryService;
    }

    public async Task JoinProjectMemory(string projectId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"memory-{projectId}");

        var memory = await _memoryService.GetProjectMemoryAsync(projectId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("memoryLoaded", memory);
    }

    public async Task UpdateMemory(string projectId, MemoryUpdate update)
    {
        await _memoryService.UpdateProjectMemoryAsync(projectId, update, Context.ConnectionAborted);

        await Clients.Group($"memory-{projectId}")
            .SendAsync("memoryUpdated", update);
    }
}
