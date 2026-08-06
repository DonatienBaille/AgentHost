using System.Security.Cryptography;
using AgentHost.Api.Contracts;
using AgentHost.Api.Domain;
using AgentHost.Api.Infrastructure;
using AgentHost.Api.Repositories;
using AgentHost.Api.Services;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

namespace AgentHost.Api.Tests.Integration;

/// <summary>
/// La rotation de clé, exercée contre la vraie base plutôt qu'en mémoire : c'est là que le
/// scénario a un sens, puisque tout l'enjeu est l'état persisté.
///
/// Le déroulé complet est rejoué : matériel écrit avec une clé jetable, service reconfiguré avec
/// cette clé en repli, rechiffrement, puis vérification que **la clé jetable peut disparaître**
/// sans rien casser. C'est la dernière étape qui compte — tant que l'ancienne clé reste
/// configurée, une clé fuitée reste exploitable, et la rotation n'a rien réglé.
///
/// Les deux colonnes chiffrées sont couvertes. En oublier une — <c>user_mfa.secret_encrypted</c>
/// est la candidate évidente — reviendrait à croire la rotation terminée, retirer l'ancienne clé,
/// et découvrir au premier login que tous les comptes à second facteur sont verrouillés.
///
/// <b>La cible du rechiffrement est toujours la clé de l'hôte de test</b>, jamais une clé jetable.
/// Le rechiffrement porte sur l'instance entière : viser une clé aléatoire réécrirait aussi les
/// secrets écrits par les autres classes de test, qui deviendraient illisibles pour l'application
/// et feraient échouer ces tests-là selon l'ordre d'exécution. C'est le même piège en production —
/// la commande n'est pas scopée, et c'est voulu. (Constaté en écrivant ces tests : une première
/// version visait une clé aléatoire et a rendu illisibles 300 secrets de la base partagée.)
///
/// Corollaire : les assertions portent sur <b>la ligne écrite par le test</b>, jamais sur les
/// compteurs globaux du rapport. La base est partagée et peut contenir des résidus d'exécutions
/// précédentes ; exiger `Failed == 0` sur l'instance entière ferait échouer un test correct à
/// cause d'une ligne dont il n'est pas responsable.
/// </summary>
[Collection(IntegrationCollection.Name)]
public class SecretsRekeyTests
{
    private readonly AgentHostApiFactory _factory;

    public SecretsRekeyTests(AgentHostApiFactory factory) => _factory = factory;

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>La clé dont l'hôte de test se sert : la cible obligatoire de tout rechiffrement ici.</summary>
    private string HostKey
    {
        get
        {
            using var scope = _factory.Services.CreateScope();
            var key = scope.ServiceProvider.GetRequiredService<IConfiguration>()["Secrets:EncryptionKey"];
            Assert.False(string.IsNullOrWhiteSpace(key),
                "L'hôte de test doit avoir une Secrets:EncryptionKey stable, sinon rien n'est rechiffrable.");
            return key!;
        }
    }

    [Fact]
    public async Task Rekeying_lets_the_old_key_be_retired_without_losing_a_secret()
    {
        var oldKey = NewKey();
        var suffix = TestData.Suffix();

        // --- avant la rotation : une valeur écrite avec l'ancienne clé ---
        var (secretId, _) = await InsertSecretAsync(suffix, "ghp_supersecret", Broker(oldKey));

        // --- pendant : clé courante de l'hôte, ancienne acceptée en déchiffrement ---
        var rotating = Broker(HostKey, oldKey);
        Assert.Equal("ghp_supersecret", rotating.Decrypt(await CiphertextAsync(secretId)));

        var report = await RekeyAsync(rotating);
        Assert.True(report.Rewritten >= 1, "au moins la valeur écrite avec l'ancienne clé devait être réécrite");

        // --- après : l'ancienne clé n'est plus configurée du tout ---
        Assert.Equal("ghp_supersecret", Broker(HostKey).Decrypt(await CiphertextAsync(secretId)));
    }

    [Fact]
    public async Task Rekeying_covers_the_totp_seeds_too_not_just_the_secrets()
    {
        var oldKey = NewKey();
        var suffix = TestData.Suffix();

        var auth = await TestData.RegisterAsync(_factory.CreateClient(), suffix);
        var seed = "JBSWY3DPEHPK3PXP";
        await InsertMfaAsync(auth.User.Id, auth.User.OrgId, Broker(oldKey).Encrypt(seed));

        await RekeyAsync(Broker(HostKey, oldKey));

        // Sans cette couverture, retirer l'ancienne clé verrouillerait tous les comptes MFA.
        Assert.Equal(seed, Broker(HostKey).Decrypt(await MfaCiphertextAsync(auth.User.Id)));
    }

    [Fact]
    public async Task Rekeying_twice_is_a_no_op_the_second_time()
    {
        var oldKey = NewKey();
        await InsertSecretAsync(TestData.Suffix(), "value", Broker(oldKey));

        var first = await RekeyAsync(Broker(HostKey, oldKey));
        var second = await RekeyAsync(Broker(HostKey, oldKey));

        // Idempotence : on doit pouvoir relancer la commande après une interruption sans crainte.
        Assert.True(first.Rewritten >= 1);
        Assert.Equal(0, second.Rewritten);
    }

    [Fact]
    public async Task A_value_no_configured_key_can_read_is_reported_as_failed_not_destroyed()
    {
        var unknownKey = NewKey();
        var (secretId, _) = await InsertSecretAsync(TestData.Suffix(), "orphan", Broker(unknownKey));
        var before = await CiphertextAsync(secretId);

        // Clé manquante dans la configuration : la commande doit le dire et sortir non nul, pas
        // écraser la ligne ni s'arrêter sur la première erreur.
        var report = await RekeyAsync(Broker(HostKey));

        Assert.True(report.Failed >= 1, "la valeur illisible devait être comptée en échec");
        // Intact : la commande ne détruit pas ce qu'elle ne sait pas lire.
        Assert.Equal(before, await CiphertextAsync(secretId));

        // Et rien n'est perdu : fournir la clé manquante rattrape la ligne à la passe suivante.
        await RekeyAsync(Broker(HostKey, unknownKey));
        Assert.Equal("orphan", Broker(HostKey).Decrypt(await CiphertextAsync(secretId)));
    }

    // ---- helpers ----

    private SecretsBroker Broker(string currentKey, params string[] previousKeys)
    {
        var settings = new Dictionary<string, string?> { ["Secrets:EncryptionKey"] = currentKey };
        for (var i = 0; i < previousKeys.Length; i++)
            settings[$"Secrets:PreviousEncryptionKeys:{i}"] = previousKeys[i];

        using var scope = _factory.Services.CreateScope();
        return new SecretsBroker(
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            scope.ServiceProvider.GetRequiredService<ISecretRepository>(),
            Log.Logger);
    }

    private async Task<RekeyReport> RekeyAsync(ISecretsBroker broker)
    {
        using var scope = _factory.Services.CreateScope();
        var service = new SecretsRekeyService(
            scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>(), broker, Log.Logger);
        return await service.RekeyAllAsync();
    }

    /// <summary>
    /// Écrit directement le chiffré : le but est de contrôler avec quelle clé la valeur a été
    /// produite, ce que l'API — qui utilise toujours la clé du processus — ne permet pas.
    /// </summary>
    private async Task<(string SecretId, string OrgId)> InsertSecretAsync(
        string suffix, string plaintext, ISecretsBroker broker)
    {
        var auth = await TestData.RegisterAsync(_factory.CreateClient(), suffix);

        using var scope = _factory.Services.CreateScope();
        var secrets = scope.ServiceProvider.GetRequiredService<ISecretRepository>();

        var secret = new Secret
        {
            Id = UlidGenerator.NewUlid(),
            OrgId = auth.User.OrgId,
            Name = $"TOKEN_{suffix.ToUpperInvariant()}",
            EncryptedValue = broker.Encrypt(plaintext),
            Scope = SecretScope.Org,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        await secrets.InsertAsync(secret);
        return (secret.Id, auth.User.OrgId);
    }

    private async Task InsertMfaAsync(string userId, string orgId, byte[] encryptedSeed)
    {
        using var scope = _factory.Services.CreateScope();
        var mfa = scope.ServiceProvider.GetRequiredService<IUserMfaRepository>();
        await mfa.UpsertAsync(new UserMfa
        {
            UserId = userId,
            OrgId = orgId,
            SecretEncrypted = encryptedSeed,
            Enabled = false,
            CreatedAt = DateTime.UtcNow,
        });
    }

    private Task<byte[]> CiphertextAsync(string secretId) =>
        BytesAsync("SELECT encrypted_value FROM secrets WHERE id = @Id", new { Id = secretId });

    private Task<byte[]> MfaCiphertextAsync(string userId) =>
        BytesAsync("SELECT secret_encrypted FROM user_mfa WHERE user_id = @Id", new { Id = userId });

    private async Task<byte[]> BytesAsync(string sql, object parameters)
    {
        using var scope = _factory.Services.CreateScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbConnectionFactory>();
        using var db = factory.CreateConnection();
        var value = await db.ExecuteScalarAsync<byte[]?>(sql, parameters);
        Assert.NotNull(value);
        return value!;
    }
}
