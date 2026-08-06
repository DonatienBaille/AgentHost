using AgentHost.Api.Infrastructure;
using Dapper;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>Ce qu'une passe de rechiffrement a réellement fait, par table.</summary>
public record RekeyReport(int Examined, int Rewritten, int AlreadyCurrent, int Failed)
{
    public static RekeyReport operator +(RekeyReport a, RekeyReport b) => new(
        a.Examined + b.Examined,
        a.Rewritten + b.Rewritten,
        a.AlreadyCurrent + b.AlreadyCurrent,
        a.Failed + b.Failed);
}

public interface ISecretsRekeyService
{
    /// <summary>Réécrit tout le matériel chiffré avec la clé courante. Idempotent.</summary>
    Task<RekeyReport> RekeyAllAsync(CancellationToken ct = default);
}

/// <summary>
/// Rechiffrement de tout le matériel secret avec la clé courante, second temps d'une rotation de
/// clé (voir <see cref="SecretsBroker"/> pour le déroulé complet).
///
/// Deux colonnes sont concernées, et il faut les deux : <c>secrets.encrypted_value</c> et
/// <c>user_mfa.secret_encrypted</c>. En oublier une reviendrait à croire la rotation terminée puis
/// à retirer l'ancienne clé — et à découvrir au premier login MFA que les seeds TOTP sont devenus
/// illisibles, c'est-à-dire que tous les comptes à second facteur sont verrouillés.
///
/// <b>Pourquoi du SQL direct et pas les dépôts.</b> Les dépôts sont scopés par organisation, ce qui
/// est exactement ce qu'il faut pour du trafic applicatif et exactement ce qui gêne ici : la
/// rotation est une opération d'exploitation qui porte sur l'instance entière, sans appelant ni
/// tenant. Passer par eux demanderait d'énumérer les organisations pour reconstituer un « tout »,
/// et manquerait les lignes de <c>user_mfa</c>, qui n'ont pas d'org_id.
///
/// <b>Idempotent</b> : une ligne déjà chiffrée avec la clé courante est laissée telle quelle. On
/// peut donc relancer la commande sans risque après une interruption.
///
/// Les secrets supprimés logiquement (<c>deleted_at</c> non nul) sont rechiffrés eux aussi. Les
/// ignorer laisserait des lignes définitivement illisibles dès le retrait de l'ancienne clé — une
/// mine pour qui voudrait plus tard restaurer ou auditer.
/// </summary>
public class SecretsRekeyService : ISecretsRekeyService
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ISecretsBroker _broker;
    private readonly ILogger _logger;

    public SecretsRekeyService(IDbConnectionFactory connectionFactory, ISecretsBroker broker, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _broker = broker;
        _logger = logger;
    }

    public async Task<RekeyReport> RekeyAllAsync(CancellationToken ct = default)
    {
        var secrets = await RekeyTableAsync(
            "secrets", "encrypted_value",
            "SELECT id, encrypted_value AS Value FROM secrets WHERE encrypted_value IS NOT NULL",
            "UPDATE secrets SET encrypted_value = @Value, updated_at = NOW() WHERE id = @Id",
            ct);

        var mfa = await RekeyTableAsync(
            "user_mfa", "secret_encrypted",
            "SELECT user_id AS id, secret_encrypted AS Value FROM user_mfa",
            "UPDATE user_mfa SET secret_encrypted = @Value WHERE user_id = @Id",
            ct);

        var total = secrets + mfa;
        _logger.Information(
            "Rechiffrement terminé : {Examined} valeur(s) examinée(s), {Rewritten} réécrite(s), " +
            "{AlreadyCurrent} déjà à jour, {Failed} en échec",
            total.Examined, total.Rewritten, total.AlreadyCurrent, total.Failed);

        if (total.Failed > 0)
        {
            _logger.Error(
                "{Failed} valeur(s) n'ont pu être déchiffrées avec aucune clé configurée. NE RETIREZ PAS " +
                "Secrets:PreviousEncryptionKeys : il manque probablement une ancienne clé.", total.Failed);
        }

        return total;
    }

    private async Task<RekeyReport> RekeyTableAsync(
        string table, string column, string selectSql, string updateSql, CancellationToken ct)
    {
        using var db = _connectionFactory.CreateConnection();
        var rows = (await db.QueryAsync<EncryptedRow>(new CommandDefinition(selectSql, cancellationToken: ct)))
            .ToList();

        int rewritten = 0, alreadyCurrent = 0, failed = 0;

        foreach (var row in rows)
        {
            if (row.Value is null || row.Value.Length == 0) continue;

            if (_broker.IsEncryptedWithCurrentKey(row.Value))
            {
                alreadyCurrent++;
                continue;
            }

            try
            {
                // Le clair n'existe qu'ici, le temps d'un aller-retour, et n'est jamais journalisé.
                var plaintext = _broker.Decrypt(row.Value);
                var reencrypted = _broker.Encrypt(plaintext);

                await db.ExecuteAsync(new CommandDefinition(
                    updateSql, new { Id = row.Id, Value = reencrypted }, cancellationToken: ct));
                rewritten++;
            }
            catch (Exception ex)
            {
                // Jamais le contenu, jamais la clé : seulement de quoi retrouver la ligne.
                _logger.Error(ex, "Rechiffrement impossible pour {Table}.{Column} id={Id}", table, column, row.Id);
                failed++;
            }
        }

        _logger.Information(
            "{Table}.{Column} : {Examined} examinée(s), {Rewritten} réécrite(s), {AlreadyCurrent} déjà à jour, " +
            "{Failed} en échec", table, column, rows.Count, rewritten, alreadyCurrent, failed);

        return new RekeyReport(rows.Count, rewritten, alreadyCurrent, failed);
    }

    private sealed class EncryptedRow
    {
        public string Id { get; set; } = string.Empty;
        public byte[]? Value { get; set; }
    }
}
