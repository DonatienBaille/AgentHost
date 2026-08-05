using System.Text.Json.Serialization;

namespace AgentHost.Api.Domain;

/// <summary>
/// A user's TOTP enrollment (user_mfa table).
///
/// <see cref="SecretEncrypted"/> is AES-256-GCM ciphertext (ISecretsBroker), never plaintext and
/// never serialized: a TOTP seed is symmetric, so whoever can read it can mint valid codes
/// indefinitely. <see cref="Enabled"/> is false between enrollment and confirmation, which is what
/// stops a mis-scanned QR code from locking a user out of their own account.
/// </summary>
public class UserMfa
{
    public string UserId { get; set; } = string.Empty;
    public string OrgId { get; set; } = string.Empty;

    [JsonIgnore]
    public byte[] SecretEncrypted { get; set; } = Array.Empty<byte>();

    /// <summary>True only after a correct code has confirmed the enrollment. Login step-up keys on this.</summary>
    public bool Enabled { get; set; }

    public DateTime? ConfirmedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One single-use recovery code (mfa_recovery_codes table). Stored as a SHA-256 hash of the
/// normalized code; the raw values are shown once, at confirmation, and never again.
/// </summary>
public class MfaRecoveryCode
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;

    [JsonIgnore]
    public string CodeHash { get; set; } = string.Empty;

    public DateTime? UsedAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
