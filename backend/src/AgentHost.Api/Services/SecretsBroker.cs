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

    /// <summary>
    /// Déchiffre une valeur stockée, avec la clé courante ou l'une des clés précédentes.
    /// </summary>
    string Decrypt(byte[] encryptedValue);

    /// <summary>
    /// Vrai si la valeur se déchiffre avec la clé <b>courante</b>. Sert à la rotation : une valeur
    /// qui répond faux est encore chiffrée avec une ancienne clé et doit être réécrite.
    /// </summary>
    bool IsEncryptedWithCurrentKey(byte[] encryptedValue);

    /// <summary>Nombre de clés précédentes acceptées en déchiffrement (0 = aucune rotation en cours).</summary>
    int PreviousKeyCount { get; }

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
///
/// <b>Rotation de clé.</b> Le chiffrement utilise toujours <c>Secrets:EncryptionKey</c>. Le
/// déchiffrement essaie cette clé, puis chacune des <c>Secrets:PreviousEncryptionKeys</c>. Sans ce
/// repli, changer la clé rendrait d'un coup illisibles tous les secrets et tous les seeds TOTP
/// déjà stockés : une fuite de clé imposerait un rechiffrement manuel hors ligne, donc une coupure.
///
/// Le déroulé prévu est : (1) ajouter l'ancienne clé aux clés précédentes et mettre la nouvelle en
/// clé courante, redéployer — le service lit tout et écrit avec la nouvelle ; (2) lancer
/// <c>--rekey-secrets</c> pour réécrire l'existant ; (3) retirer l'ancienne clé de la
/// configuration. C'est l'étape 3 qui rend la clé fuitée réellement inutile, et elle n'est sûre
/// qu'une fois l'étape 2 terminée.
/// </summary>
public class SecretsBroker : ISecretsBroker
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    /// <summary>Clés acceptées en déchiffrement uniquement, dans l'ordre de la configuration.</summary>
    private readonly byte[][] _previousKeys;

    private readonly ISecretRepository _secretRepository;
    private readonly ILogger _logger;

    public SecretsBroker(IConfiguration config, ISecretRepository secretRepository, ILogger logger)
    {
        _secretRepository = secretRepository;
        _logger = logger;
        _previousKeys = ReadPreviousKeys(config, logger);

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

        if (_previousKeys.Length > 0)
        {
            _logger.Warning(
                "Rotation de la clé de chiffrement en cours : {Count} clé(s) précédente(s) encore acceptée(s) en " +
                "déchiffrement. Lancez --rekey-secrets puis retirez Secrets:PreviousEncryptionKeys — tant qu'elles " +
                "sont configurées, une clé fuitée reste exploitable.",
                _previousKeys.Length);
        }
    }

    public int PreviousKeyCount => _previousKeys.Length;

    private static byte[][] ReadPreviousKeys(IConfiguration config, ILogger logger)
    {
        var configured = config.GetSection("Secrets:PreviousEncryptionKeys").Get<string[]>() ?? [];
        var keys = new List<byte[]>();

        foreach (var value in configured)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            var key = Convert.FromBase64String(value);
            if (key.Length != 32)
            {
                // Refus explicite : une clé mal formée passée inaperçue transformerait la rotation
                // en perte de données silencieuse, découverte au premier secret illisible.
                throw new InvalidOperationException(
                    "Every Secrets:PreviousEncryptionKeys entry must decode to exactly 32 bytes (AES-256)");
            }
            keys.Add(key);
        }

        return keys.ToArray();
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

        if (TryDecrypt(encryptedValue, _key, out var plaintext)) return plaintext;

        // Le tag GCM est ce qui rend l'essai des clés sûr : une mauvaise clé échoue à
        // l'authentification, elle ne rend jamais un clair plausible mais faux.
        foreach (var previous in _previousKeys)
        {
            if (TryDecrypt(encryptedValue, previous, out plaintext)) return plaintext;
        }

        throw new CryptographicException(
            _previousKeys.Length == 0
                ? "Impossible de déchiffrer avec Secrets:EncryptionKey. Si la clé vient de changer, l'ancienne doit " +
                  "figurer dans Secrets:PreviousEncryptionKeys jusqu'à la fin du rechiffrement (--rekey-secrets)."
                : $"Impossible de déchiffrer avec la clé courante ni avec les {_previousKeys.Length} clé(s) " +
                  "précédente(s) configurée(s).");
    }

    public bool IsEncryptedWithCurrentKey(byte[] encryptedValue) =>
        encryptedValue.Length >= NonceSize + TagSize && TryDecrypt(encryptedValue, _key, out _);

    private static bool TryDecrypt(byte[] encryptedValue, byte[] key, out string plaintext)
    {
        var nonce = encryptedValue[..NonceSize];
        var tag = encryptedValue[^TagSize..];
        var ciphertext = encryptedValue[NonceSize..^TagSize];
        var plaintextBytes = new byte[ciphertext.Length];

        try
        {
            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintextBytes);
        }
        catch (CryptographicException)
        {
            plaintext = string.Empty;
            return false;
        }

        plaintext = Encoding.UTF8.GetString(plaintextBytes);
        return true;
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
