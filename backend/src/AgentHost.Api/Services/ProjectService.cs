using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

public interface IProjectService
{
    Task<Project?> GetAsync(string id, CancellationToken ct = default);
    Task<List<Project>> ListAsync(CancellationToken ct = default);
    Task<List<Project>> ListByOrgAsync(string orgId, CancellationToken ct = default);
    Task<Project> CreateAsync(CreateProjectRequest req, CancellationToken ct = default);
    Task<Project?> UpdateAsync(string id, UpdateProjectRequest req, CancellationToken ct = default);
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

    public Task<Project?> GetAsync(string id, CancellationToken ct = default) => _projectRepository.GetAsync(id, ct);

    public Task<List<Project>> ListAsync(CancellationToken ct = default) => _projectRepository.ListAsync(ct);

    public Task<List<Project>> ListByOrgAsync(string orgId, CancellationToken ct = default) =>
        _projectRepository.ListByOrgAsync(orgId, ct);

    public async Task<Project> CreateAsync(CreateProjectRequest req, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var project = new Project
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = req.OrgId,
            Name = req.Name,
            Slug = req.Slug,
            Description = req.Description,
            BudgetMonthlyUsd = req.BudgetMonthlyUsd,
            CreatedAt = now,
            UpdatedAt = now,
        };

        await _projectRepository.InsertAsync(project, ct);
        _logger.Information("Created project {ProjectId} ({Slug})", project.Id, project.Slug);
        return project;
    }

    public async Task<Project?> UpdateAsync(string id, UpdateProjectRequest req, CancellationToken ct = default)
    {
        var project = await _projectRepository.GetAsync(id, ct);
        if (project is null) return null;

        if (req.Name is not null) project.Name = req.Name;
        if (req.Description is not null) project.Description = req.Description;
        if (req.BudgetMonthlyUsd is not null) project.BudgetMonthlyUsd = req.BudgetMonthlyUsd.Value;
        project.UpdatedAt = DateTime.UtcNow;

        await _projectRepository.UpdateAsync(project, ct);
        return project;
    }
}
