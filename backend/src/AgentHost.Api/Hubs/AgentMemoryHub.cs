using AgentHost.Api.Contracts;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace AgentHost.Api.Hubs;

/// <summary>
/// Real-time hub for collaborative editing of a project's agentic memory (spec section 10.3).
///
/// Both methods verify the project belongs to the caller's organization first. <see
/// cref="UpdateMemory"/> is a *write*, so an unchecked call let any authenticated user rewrite
/// another tenant's project context and learnings — which subsequent runs then act on.
/// </summary>
public class AgentMemoryHub : Hub
{
    private readonly IMemoryService _memoryService;
    private readonly IProjectRepository _projectRepository;
    private readonly ILogger _logger;

    public AgentMemoryHub(IMemoryService memoryService, IProjectRepository projectRepository, ILogger logger)
    {
        _memoryService = memoryService;
        _projectRepository = projectRepository;
        _logger = logger;
    }

    public async Task JoinProjectMemory(string projectId)
    {
        if (!await IsAuthorizedForProjectAsync(projectId)) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"memory-{projectId}");

        var memory = await _memoryService.GetProjectMemoryAsync(projectId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("memoryLoaded", memory);
    }

    public async Task UpdateMemory(string projectId, MemoryUpdate update)
    {
        if (!await IsAuthorizedForProjectAsync(projectId)) return;

        await _memoryService.UpdateProjectMemoryAsync(projectId, update, Context.ConnectionAborted);

        await Clients.Group($"memory-{projectId}")
            .SendAsync("memoryUpdated", update);
    }

    /// <summary>
    /// True when the project belongs to the caller's org. Otherwise sends an <c>error</c> to the
    /// caller — worded identically to "no such project" so ids can't be probed for existence.
    /// </summary>
    private async Task<bool> IsAuthorizedForProjectAsync(string projectId)
    {
        var orgId = Context.User.GetOrgId();
        if (string.IsNullOrEmpty(orgId))
        {
            await Clients.Caller.SendAsync("error", "Not authenticated");
            return false;
        }

        if (await _projectRepository.GetAsync(projectId, orgId, Context.ConnectionAborted) is null)
        {
            _logger.Warning("Connection {ConnectionId} (org {OrgId}) was denied access to project memory {ProjectId}",
                Context.ConnectionId, orgId, projectId);
            await Clients.Caller.SendAsync("error", $"Project {projectId} not found");
            return false;
        }

        return true;
    }
}
