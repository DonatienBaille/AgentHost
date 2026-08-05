namespace AgentHost.Api.Contracts;

public class CreateWebhookRequest
{
    public string ProjectId { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> Events { get; set; } = new();
    public string? SecretToken { get; set; }
}
