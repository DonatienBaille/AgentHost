namespace AgentHost.Api.Contracts;

public class CreateProjectRequest
{
    public string OrgId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal BudgetMonthlyUsd { get; set; } = 1000m;
}

public class UpdateProjectRequest
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public decimal? BudgetMonthlyUsd { get; set; }
}
