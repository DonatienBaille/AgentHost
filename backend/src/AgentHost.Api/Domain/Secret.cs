namespace AgentHost.Api.Domain;

/// <summary>Encrypted secret (secrets table). encrypted_value holds AES-GCM ciphertext.</summary>
public class Secret
{
    public string Id { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;
    public string? ProjectId { get; set; }

    public string Name { get; set; } = string.Empty;

    public byte[]? EncryptedValue { get; set; } // nonce(12) || ciphertext || tag(16), see SecretsBroker
    public string? VaultPath { get; set; } // when Vault backend enabled
    public string? DigestSha256Truncated { get; set; } // for leak detection

    public SecretScope Scope { get; set; } = SecretScope.Org;

    public DateTime? LastUsedAt { get; set; }
    public string? LastUsedByRunId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
