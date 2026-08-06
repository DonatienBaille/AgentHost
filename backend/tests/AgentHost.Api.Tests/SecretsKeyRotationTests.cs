using System.Security.Cryptography;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Microsoft.Extensions.Configuration;
using Serilog;
using Xunit;

namespace AgentHost.Api.Tests;

/// <summary>
/// Rotation de la clé de chiffrement des secrets.
///
/// Sans repli sur les clés précédentes, changer <c>Secrets:EncryptionKey</c> rendait d'un coup
/// illisibles tous les secrets stockés ET tous les seeds TOTP — donc une fuite de clé imposait un
/// rechiffrement manuel hors ligne, c'est-à-dire une coupure. Ces tests portent sur la propriété
/// qui rend la rotation praticable : pendant la fenêtre de rotation, l'ancien et le nouveau
/// coexistent ; après le rechiffrement, l'ancienne clé peut disparaître sans rien casser.
///
/// Le tag GCM est ce qui rend l'essai successif des clés sûr : une mauvaise clé échoue à
/// l'authentification, elle ne produit jamais un clair plausible mais faux. Le dernier test le
/// vérifie explicitement plutôt que de le tenir pour acquis.
/// </summary>
public class SecretsKeyRotationTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static SecretsBroker Broker(string currentKey, params string[] previousKeys)
    {
        var settings = new Dictionary<string, string?> { ["Secrets:EncryptionKey"] = currentKey };
        for (var i = 0; i < previousKeys.Length; i++)
            settings[$"Secrets:PreviousEncryptionKeys:{i}"] = previousKeys[i];

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new SecretsBroker(config, new UnusedSecretRepository(), Log.Logger);
    }

    [Fact]
    public void A_value_encrypted_with_the_current_key_round_trips()
    {
        var key = NewKey();
        var broker = Broker(key);

        var ciphertext = broker.Encrypt("ghp_supersecret");

        Assert.Equal("ghp_supersecret", broker.Decrypt(ciphertext));
        Assert.True(broker.IsEncryptedWithCurrentKey(ciphertext));
        Assert.Equal(0, broker.PreviousKeyCount);
    }

    [Fact]
    public void A_value_encrypted_with_the_old_key_is_still_readable_during_rotation()
    {
        var oldKey = NewKey();
        var newKey = NewKey();

        var legacy = Broker(oldKey).Encrypt("ghp_supersecret");
        var rotating = Broker(newKey, oldKey);

        // C'est toute la fenêtre de rotation : le service lit l'ancien et écrit le nouveau.
        Assert.Equal("ghp_supersecret", rotating.Decrypt(legacy));
        Assert.False(rotating.IsEncryptedWithCurrentKey(legacy));
        Assert.Equal(1, rotating.PreviousKeyCount);
    }

    [Fact]
    public void Dropping_the_old_key_before_rekeying_makes_the_value_unreadable()
    {
        var oldKey = NewKey();
        var newKey = NewKey();
        var legacy = Broker(oldKey).Encrypt("ghp_supersecret");

        // C'est l'erreur d'exploitation que la documentation doit éviter : retirer l'ancienne clé
        // avant d'avoir rechiffré. Le test la fige pour qu'elle échoue bruyamment, pas en silence.
        var error = Assert.Throws<CryptographicException>(() => Broker(newKey).Decrypt(legacy));
        Assert.Contains("PreviousEncryptionKeys", error.Message);
    }

    [Fact]
    public void Several_previous_keys_are_all_accepted()
    {
        var oldest = NewKey();
        var middle = NewKey();
        var current = NewKey();

        var fromOldest = Broker(oldest).Encrypt("a");
        var fromMiddle = Broker(middle).Encrypt("b");

        // Deux rotations enchaînées sans rechiffrement entre les deux : les deux générations
        // doivent rester lisibles.
        var broker = Broker(current, oldest, middle);
        Assert.Equal("a", broker.Decrypt(fromOldest));
        Assert.Equal("b", broker.Decrypt(fromMiddle));
        Assert.Equal("c", broker.Decrypt(broker.Encrypt("c")));
    }

    [Fact]
    public void A_new_encryption_always_uses_the_current_key()
    {
        var oldKey = NewKey();
        var newKey = NewKey();
        var rotating = Broker(newKey, oldKey);

        var fresh = rotating.Encrypt("written during rotation");

        // Écrit avec la nouvelle : une fois l'ancienne retirée, la valeur reste lisible.
        Assert.True(rotating.IsEncryptedWithCurrentKey(fresh));
        Assert.Equal("written during rotation", Broker(newKey).Decrypt(fresh));
    }

    [Fact]
    public void A_wrong_key_is_rejected_rather_than_producing_plausible_garbage()
    {
        var ciphertext = Broker(NewKey()).Encrypt("ghp_supersecret");

        // La propriété qui rend l'essai successif des clés légitime : sans authentification du
        // chiffré, essayer des clés rendrait des clairs faux et l'API livrerait des secrets erronés
        // aux agents sans que rien ne signale l'erreur.
        Assert.Throws<CryptographicException>(() => Broker(NewKey()).Decrypt(ciphertext));
    }

    [Fact]
    public void A_malformed_previous_key_is_refused_at_startup()
    {
        // Passer inaperçue, une clé mal formée transformerait la rotation en perte de données
        // découverte au premier secret illisible. Mieux vaut refuser de démarrer.
        var error = Assert.Throws<InvalidOperationException>(() =>
            Broker(NewKey(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

        Assert.Contains("32 bytes", error.Message);
    }

    [Fact]
    public void An_empty_previous_key_entry_is_ignored_rather_than_fatal()
    {
        // Les listes de configuration héritées d'un template ou d'une variable d'environnement vide
        // contiennent souvent une entrée vide ; ce n'est pas une clé mal formée, c'est une absence.
        var broker = Broker(NewKey(), "");
        Assert.Equal(0, broker.PreviousKeyCount);
    }

    /// <summary>
    /// <see cref="SecretsBroker"/> exige un dépôt pour <c>ResolveForRunAsync</c>, que ces tests
    /// n'exercent pas — ils portent sur la cryptographie seule.
    /// </summary>
    private sealed class UnusedSecretRepository : ISecretRepository
    {
        private static Exception Unused() => new NotSupportedException("Non utilisé par ces tests");

        public Task<Api.Domain.Secret?> GetAsync(string id, string orgId, CancellationToken ct = default) => throw Unused();
        public Task<List<Api.Domain.Secret>> ListByOrgAsync(string orgId, int skip = 0, int take = 200, CancellationToken ct = default) => throw Unused();
        public Task<Api.Domain.Secret?> GetByNameAsync(string orgId, string name, CancellationToken ct = default) => throw Unused();
        public Task<List<Api.Domain.Secret>> ListForScopeAsync(string orgId, string? projectId, IEnumerable<string> names, CancellationToken ct = default) => throw Unused();
        public Task InsertAsync(Api.Domain.Secret secret, CancellationToken ct = default) => throw Unused();
        public Task UpdateAsync(Api.Domain.Secret secret, CancellationToken ct = default) => throw Unused();
        public Task MarkUsedAsync(string id, string runId, CancellationToken ct = default) => throw Unused();
        public Task SoftDeleteAsync(string id, string orgId, CancellationToken ct = default) => throw Unused();
    }
}
