namespace AgentHost.Api.Domain;

public class Webhook
{
    public string Id { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;
    public List<string> Events { get; set; } = new(); // ["run.created", "run.finished"]

    public string? SecretToken { get; set; } // for HMAC signing

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
