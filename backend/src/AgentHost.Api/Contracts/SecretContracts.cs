using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

public class CreateSecretRequest
{
    public string OrgId { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty; // plaintext, encrypted server-side before storage
    public SecretScope Scope { get; set; } = SecretScope.Org;
}
