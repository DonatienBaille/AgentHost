namespace AgentHost.Api.Contracts;

public class CreateProjectRequest
{
    // No OrgId: the owning organization comes from the caller's JWT (ICallerContext.OrgId).
    // Accepting it from the body let any authenticated user plant a project in someone else's org.
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
