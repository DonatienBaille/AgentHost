namespace AgentHost.Api.Contracts;

public class CreateOrganizationRequest
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string Plan { get; set; } = "free";
}

public class UpdateOrganizationRequest
{
    public string? Name { get; set; }
    public string? Plan { get; set; }
}
