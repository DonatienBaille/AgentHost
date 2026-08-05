using System.Security.Cryptography;
using System.Text;
using AgentHost.Api.Domain;
using AgentHost.Api.Repositories;
using Serilog;

namespace AgentHost.Api.Services;

public interface ISecretsBroker
{
    /// <summary>Encrypts a plaintext secret value for storage (AES-GCM, key from config).</summary>
    byte[] Encrypt(string plaintext);

    /// <summary>Decrypts a stored secret value.</summary>
    string Decrypt(byte[] encryptedValue);

    /// <summary>
    /// Resolves the plaintext secret values a run is allowed to see: secrets whose name is
    /// listed in the agent manifest's `permissions.secrets`, scoped to the run's org/project.
    /// Marks each resolved secret's `last_used_at`/`last_used_by_run_id`.
    /// </summary>
    Task<Dictionary<string, string>> ResolveForRunAsync(
        string orgId, string? projectId, string runId, IEnumerable<string> secretNames, CancellationToken ct = default);
}

/// <summary>
/// Default Postgres-encrypted secrets backend (spec section 13.1, T3). AES-256-GCM with a
/// 96-bit random nonce per encryption; storage layout is nonce(12) || ciphertext || tag(16).
/// </summary>
public class SecretsBroker : ISecretsBroker
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;
    private readonly ISecretRepository _secretRepository;
    private readonly ILogger _logger;

    public SecretsBroker(IConfiguration config, ISecretRepository secretRepository, ILogger logger)
    {
        _secretRepository = secretRepository;
        _logger = logger;

        var keyBase64 = config["Secrets:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(keyBase64))
        {
            // Dev-only fallback so `dotnet run` works without extra setup; production deployments
            // must set Secrets__EncryptionKey to a stable base64-encoded 32-byte key.
            _logger.Warning("Secrets:EncryptionKey is not configured; using an ephemeral dev-only key. " +
                             "Secrets encrypted this run will NOT be decryptable after restart.");
            _key = RandomNumberGenerator.GetBytes(32);
            return;
        }

        _key = Convert.FromBase64String(keyBase64);
        if (_key.Length != 32)
            throw new InvalidOperationException("Secrets:EncryptionKey must decode to exactly 32 bytes (AES-256)");
    }

    public byte[] Encrypt(string plaintext)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var result = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);
        return result;
    }

    public string Decrypt(byte[] encryptedValue)
    {
        if (encryptedValue.Length < NonceSize + TagSize)
            throw new ArgumentException("Encrypted value is too short to contain nonce + tag", nameof(encryptedValue));

        var nonce = encryptedValue[..NonceSize];
        var tag = encryptedValue[^TagSize..];
        var ciphertext = encryptedValue[NonceSize..^TagSize];
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    public async Task<Dictionary<string, string>> ResolveForRunAsync(
        string orgId, string? projectId, string runId, IEnumerable<string> secretNames, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        var names = secretNames.Distinct().ToList();
        if (names.Count == 0) return result;

        var secrets = await _secretRepository.ListForScopeAsync(orgId, projectId, names, ct);

        foreach (var secret in secrets)
        {
            if (secret.EncryptedValue is null || secret.EncryptedValue.Length == 0)
            {
                _logger.Warning("Secret {SecretId} ({Name}) has no encrypted value stored; skipping", secret.Id, secret.Name);
                continue;
            }

            try
            {
                result[secret.Name] = Decrypt(secret.EncryptedValue);
                await _secretRepository.MarkUsedAsync(secret.Id, runId, ct);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to decrypt secret {SecretId} ({Name})", secret.Id, secret.Name);
            }
        }

        return result;
    }
}
