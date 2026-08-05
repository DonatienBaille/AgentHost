using AgentHost.Api.Domain;

namespace AgentHost.Api.Contracts;

public class CreateSecretRequest
{
    // No OrgId: taken from the caller's JWT. ProjectId, when given, is verified to belong to it.
    public string? ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty; // plaintext, encrypted server-side before storage
    public SecretScope Scope { get; set; } = SecretScope.Org;
}

/// <summary>Rotates a secret's value in place. The new value is never echoed back.</summary>
public class UpdateSecretRequest
{
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Secret metadata safe to return over the API: everything except the ciphertext, the vault path
/// and (obviously) the plaintext.
/// </summary>
public class SecretResponse
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string? ProjectId { get; set; }
    public string Name { get; set; } = string.Empty;
    public SecretScope Scope { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedByRunId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public static SecretResponse From(Secret secret) => new()
    {
        Id = secret.Id,
        OrgId = secret.OrgId,
        ProjectId = secret.ProjectId,
        Name = secret.Name,
        Scope = secret.Scope,
        LastUsedAt = secret.LastUsedAt,
        LastUsedByRunId = secret.LastUsedByRunId,
        CreatedAt = secret.CreatedAt,
        UpdatedAt = secret.UpdatedAt,
    };
}
