using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;

namespace AgentHost.Api.Hubs;

/// <summary>
/// Real-time hub for project-scoped sessions: memory + recent runs (spec section 10.2).
///
/// Joining a project streams that project's memory and run history to the caller, so the project
/// must be verified to belong to the caller's organization first — authentication alone does not
/// authorize a specific project id.
/// </summary>
public class ProjectHub : Hub
{
    private const int RecentRunCount = 10;

    private readonly IMemoryService _memoryService;
    private readonly IRunRepository _runRepository;
    private readonly IProjectRepository _projectRepository;
    private readonly ILogger _logger;

    public ProjectHub(
        IMemoryService memoryService,
        IRunRepository runRepository,
        IProjectRepository projectRepository,
        ILogger logger)
    {
        _memoryService = memoryService;
        _runRepository = runRepository;
        _projectRepository = projectRepository;
        _logger = logger;
    }

    public async Task JoinProject(string projectId)
    {
        var orgId = await ResolveAuthorizedProjectOrgAsync(projectId);
        if (orgId is null) return;

        await Groups.AddToGroupAsync(Context.ConnectionId, $"project-{projectId}");

        var memory = await _memoryService.GetProjectMemoryAsync(projectId, Context.ConnectionAborted);
        await Clients.Caller.SendAsync("projectMemory", memory);

        var recentRuns = await _runRepository.ListByProjectAsync(
            projectId, orgId, skip: 0, take: RecentRunCount, ct: Context.ConnectionAborted);
        await Clients.Caller.SendAsync("recentRuns", recentRuns);
    }

    public async Task LeaveProject(string projectId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"project-{projectId}");
    }

    /// <summary>
    /// Returns the caller's org id if the project belongs to it; otherwise sends an <c>error</c>
    /// to the caller and returns null. Indistinguishable from "no such project", by design.
    /// </summary>
    private async Task<string?> ResolveAuthorizedProjectOrgAsync(string projectId)
    {
        var orgId = Context.User.GetOrgId();
        if (string.IsNullOrEmpty(orgId))
        {
            await Clients.Caller.SendAsync("error", "Not authenticated");
            return null;
        }

        if (await _projectRepository.GetAsync(projectId, orgId, Context.ConnectionAborted) is null)
        {
            _logger.Warning("Connection {ConnectionId} (org {OrgId}) was denied access to project {ProjectId}",
                Context.ConnectionId, orgId, projectId);
            await Clients.Caller.SendAsync("error", $"Project {projectId} not found");
            return null;
        }

        return orgId;
    }
}
