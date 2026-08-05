using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>
/// Every method here takes the *caller's* organization id, sourced from the JWT by the endpoint
/// layer (ICallerContext.OrgId) and never from the request. Lookups that miss return null so the
/// endpoint answers 404 — a 403 would confirm that some other tenant owns that ULID.
/// </summary>
public interface IProjectService
{
    Task<Project?> GetAsync(string id, string orgId, CancellationToken ct = default);
    Task<List<Project>> ListByOrgAsync(string orgId, CancellationToken ct = default);
    Task<Project> CreateAsync(CreateProjectRequest req, string orgId, CancellationToken ct = default);
    Task<Project?> UpdateAsync(string id, string orgId, UpdateProjectRequest req, CancellationToken ct = default);

    /// <summary>Soft-deletes the project and cascades to its agents, runs, project secrets and webhooks.</summary>
    Task<bool> DeleteAsync(string id, string orgId, CancellationToken ct = default);
}

public class ProjectService : IProjectService
{
    private readonly IProjectRepository _projectRepository;
    private readonly ILogger _logger;

    public ProjectService(IProjectRepository projectRepository, ILogger logger)
    {
        _projectRepository = projectRepository;
        _logger = logger;
    }

    public Task<Project?> GetAsync(string id, string orgId, CancellationToken ct = default) =>
        _projectRepository.GetAsync(id, orgId, ct);

    public Task<List<Project>> ListByOrgAsync(string orgId, CancellationToken ct = default) =>
        _projectRepository.ListByOrgAsync(orgId, ct);

    public async Task<Project> CreateAsync(CreateProjectRequest req, string orgId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var project = new Project
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = orgId,
            Name = req.Name,
            Slug = req.Slug,
            Description = req.Description,
            BudgetMonthlyUsd = req.BudgetMonthlyUsd,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _projectRepository.InsertAsync(project, ct);
        _logger.Information("Created project {ProjectId} ({Slug}) in org {OrgId}", project.Id, project.Slug, orgId);
        return project;
    }

    public async Task<Project?> UpdateAsync(string id, string orgId, UpdateProjectRequest req, CancellationToken ct = default)
    {
        var project = await _projectRepository.GetAsync(id, orgId, ct);
        if (project is null) return null;

        if (req.Name is not null) project.Name = req.Name;
        if (req.Description is not null) project.Description = req.Description;
        if (req.BudgetMonthlyUsd is not null) project.BudgetMonthlyUsd = req.BudgetMonthlyUsd.Value;
        project.UpdatedAt = DateTime.UtcNow;

        await _projectRepository.UpdateAsync(project, ct);
        return project;
    }

    public async Task<bool> DeleteAsync(string id, string orgId, CancellationToken ct = default)
    {
        var project = await _projectRepository.GetAsync(id, orgId, ct);
        if (project is null) return false;

        await _projectRepository.SoftDeleteCascadeAsync(id, orgId, ct);
        return true;
    }
}
