using AgentHost.Api.Infrastructure;
using Dapper;
using Npgsql;
using Serilog;

namespace AgentHost.Api.Services;

/// <summary>Ce qu'une purge a effacé, table par table.</summary>
public sealed class OrgPurgeReport
{
    public string OrgId { get; init; } = string.Empty;

    /// <summary>Faux quand l'organisation n'existe pas : rien n'a été fait, et ce n'est pas une erreur.</summary>
    public bool Found { get; init; }

    /// <summary>Lignes supprimées, par table, dans l'ordre où elles l'ont été.</summary>
    public List<(string Table, int Rows)> Deleted { get; } = [];

    public int TotalRows => Deleted.Sum(d => d.Rows);
}

public interface IOrgPurgeService
{
    /// <summary>Efface définitivement une organisation et tout ce qui en dépend.</summary>
    Task<OrgPurgeReport> PurgeAsync(string orgId, CancellationToken ct = default);
}

/// <summary>
/// L'effacement définitif d'une organisation (dette identifiée, hors lots — « purge RGPD réelle »).
///
/// <b>Ce qui existait, et pourquoi ça ne suffisait pas.</b> La suppression d'une organisation ou
/// d'un projet posait un <c>deleted_at</c>. C'est le bon comportement pour une suppression
/// ordinaire — une erreur de manipulation se rattrape, et les runs passés gardent un sens. Ce n'est
/// pas un effacement : les adresses, les noms, les identifiants et l'activité restent en base,
/// intégralement, indéfiniment. Un droit à l'effacement auquel on répond par un drapeau n'est pas
/// honoré, il est mimé.
///
/// <b>Pourquoi c'est une commande d'exploitation et non un endpoint.</b> Comme
/// <c>--rekey-secrets</c> : il n'y a pas d'appelant à autoriser — celui dont on efface
/// l'organisation ne peut pas, par construction, être authentifié à la fin de l'opération — pas de
/// délai de requête à respecter sur une transaction qui touche vingt tables, et surtout rien à
/// exposer. Une purge accessible par HTTP est une suppression de compte à un jeton de distance.
///
/// <b>Tout ou rien.</b> Une seule transaction. Une purge à moitié faite laisserait des lignes
/// orphelines que plus aucun chemin applicatif ne sait atteindre — donc des données personnelles
/// invisibles, ce qui est pire que de ne rien avoir effacé du tout.
///
/// <b>Le journal d'audit, et la seule dérogation au WORM du dépôt.</b> <c>audit_log</c> est
/// append-only depuis la migration 0008 : trois triggers refusent <c>UPDATE</c>, <c>DELETE</c> et
/// <c>TRUNCATE</c>. Ces triggers existent parce qu'un journal effaçable ne prouve rien — et cette
/// purge doit pourtant en effacer une partie, puisqu'il porte les identifiants des membres.
///
/// L'arbitrage est explicite : la responsabilité que le journal sert à établir s'exerce
/// <i>à l'intérieur d'une organisation vivante</i>. Une fois celle-ci effacée, conserver
/// l'activité de ses membres n'est plus une garantie pour qui que ce soit — c'est exactement la
/// conservation que le droit interdit. Le trigger est donc désactivé le temps de la transaction,
/// pour les seules lignes de cette organisation, depuis un chemin hors ligne, et l'opération est
/// tracée dans le journal du service. C'est la <b>seule</b> dérogation du dépôt, et elle est ici
/// pour être lue.
/// </summary>
public class OrgPurgeService : IOrgPurgeService
{
    /// <summary>
    /// L'ordre d'effacement : des feuilles vers les racines.
    ///
    /// Il est écrit à la main plutôt que dérivé des clés étrangères, et c'est délibéré : une purge
    /// qui découvrirait l'ordre toute seule effacerait aussi ce que personne n'a relu. Chaque ligne
    /// ci-dessous est une décision. Un <c>ON DELETE CASCADE</c> aurait le même défaut en pire — il
    /// rendrait l'effacement invisible dans le code.
    ///
    /// Le tableau est confronté au schéma par un test : une table ajoutée sans être purgée y
    /// laisserait des données personnelles que plus rien ne sait atteindre.
    /// </summary>
    private static readonly (string Table, string Sql)[] Steps =
    [
        // --- verrouillage d'abord ---
        //
        // Une purge court contre l'application vivante : un run en cours écrit des événements
        // pendant qu'on efface, et le DELETE sur `runs` échoue alors sur une clé étrangère que le
        // pas précédent avait pourtant vidée. Ce n'est pas un cas de test, c'est le cas normal —
        // on efface une organisation dont l'activité ne s'est pas arrêtée d'elle-même.
        //
        // `FOR UPDATE` sur les lignes parentes est la bonne primitive : la vérification de clé
        // étrangère d'un INSERT enfant prend un `FOR KEY SHARE` sur le parent, qui entre en
        // conflit. L'écrivain concurrent attend donc la fin de la transaction, puis échoue — ce
        // qui est exact, puisque le run a disparu entre-temps.
        ("lock:runs", "SELECT id FROM runs WHERE org_id = @OrgId FOR UPDATE"),
        ("lock:projects", "SELECT id FROM projects WHERE org_id = @OrgId FOR UPDATE"),
        ("lock:agents", "SELECT id FROM agents WHERE org_id = @OrgId FOR UPDATE"),
        ("lock:users", "SELECT id FROM users WHERE org_id = @OrgId FOR UPDATE"),
        ("lock:triggers", "SELECT id FROM triggers WHERE org_id = @OrgId FOR UPDATE"),

        // --- feuilles rattachées aux runs ---
        ("run_events", """
            DELETE FROM run_events WHERE run_id IN (SELECT id FROM runs WHERE org_id = @OrgId)
            """),
        ("approvals", """
            DELETE FROM approvals WHERE run_id IN (SELECT id FROM runs WHERE org_id = @OrgId)
            """),
        ("artifacts", """
            DELETE FROM artifacts WHERE run_id IN (SELECT id FROM runs WHERE org_id = @OrgId)
            """),

        // --- feuilles rattachées aux projets ---
        ("project_memories", """
            DELETE FROM project_memories WHERE project_id IN (SELECT id FROM projects WHERE org_id = @OrgId)
            """),
        ("webhooks", """
            DELETE FROM webhooks WHERE project_id IN (SELECT id FROM projects WHERE org_id = @OrgId)
            """),
        ("trigger_deliveries", """
            DELETE FROM trigger_deliveries WHERE trigger_id IN (SELECT id FROM triggers WHERE org_id = @OrgId)
            """),
        ("triggers", "DELETE FROM triggers WHERE org_id = @OrgId"),

        // --- feuilles rattachées aux utilisateurs ---
        ("mfa_recovery_codes", """
            DELETE FROM mfa_recovery_codes WHERE user_id IN (SELECT id FROM users WHERE org_id = @OrgId)
            """),
        ("user_mfa", """
            DELETE FROM user_mfa WHERE user_id IN (SELECT id FROM users WHERE org_id = @OrgId)
            """),
        ("refresh_tokens", """
            DELETE FROM refresh_tokens WHERE user_id IN (SELECT id FROM users WHERE org_id = @OrgId)
            """),
        ("password_reset_tokens", """
            DELETE FROM password_reset_tokens WHERE user_id IN (SELECT id FROM users WHERE org_id = @OrgId)
            """),
        ("invitations", "DELETE FROM invitations WHERE org_id = @OrgId"),

        // --- le corps ---
        ("runs", "DELETE FROM runs WHERE org_id = @OrgId"),

        // `agents.current_version_id` pointe vers `agent_versions`, qui pointe vers `agents` : le
        // cycle se casse en détachant la version courante avant d'effacer les deux tables.
        ("agents.current_version_id", "UPDATE agents SET current_version_id = NULL WHERE org_id = @OrgId"),
        ("agent_versions", """
            DELETE FROM agent_versions WHERE agent_id IN (SELECT id FROM agents WHERE org_id = @OrgId)
            """),
        ("agents", "DELETE FROM agents WHERE org_id = @OrgId"),
        ("secrets", "DELETE FROM secrets WHERE org_id = @OrgId"),
        ("projects", "DELETE FROM projects WHERE org_id = @OrgId"),

        // --- l'identité ---
        ("audit_log", "DELETE FROM audit_log WHERE org_id = @OrgId"),
        ("users", "DELETE FROM users WHERE org_id = @OrgId"),
        ("organizations", "DELETE FROM organizations WHERE id = @OrgId"),
    ];

    /// <summary>Les tables que la purge doit couvrir, pour le test qui confronte ce service au schéma.</summary>
    public static IReadOnlyList<string> PurgedTables =>
        Steps.Select(s => s.Table).Where(t => !t.Contains('.') && !t.Contains(':')).ToList();

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger _logger;

    public OrgPurgeService(IDbConnectionFactory connectionFactory, ILogger logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<OrgPurgeReport> PurgeAsync(string orgId, CancellationToken ct = default)
    {
        // `CreateConnection` rend une connexion DÉJÀ ouverte (voir NpgsqlConnectionFactory) ;
        // l'ouvrir à nouveau lève « Connection already open ».
        using var db = (NpgsqlConnection)_connectionFactory.CreateConnection();

        var exists = await db.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM organizations WHERE id = @OrgId", new { OrgId = orgId }, cancellationToken: ct));

        if (exists == 0)
        {
            _logger.Warning("Purge demandée pour l'organisation {OrgId}, qui n'existe pas", orgId);
            return new OrgPurgeReport { OrgId = orgId, Found = false };
        }

        var report = new OrgPurgeReport { OrgId = orgId, Found = true };

        using var transaction = await db.BeginTransactionAsync(ct);
        try
        {
            // La dérogation au WORM, énoncée en toutes lettres dans le commentaire de classe. Le
            // trigger est rétabli dans le `finally` de la transaction — un échec ne doit pas
            // laisser le journal réécrivable pour les autres organisations.
            await db.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE audit_log DISABLE TRIGGER audit_log_no_delete",
                transaction: transaction, cancellationToken: ct));

            foreach (var (table, sql) in Steps)
            {
                var rows = await db.ExecuteAsync(new CommandDefinition(
                    sql, new { OrgId = orgId }, transaction: transaction, cancellationToken: ct));

                // Les verrous ne suppriment rien : les compter fausserait le rapport.
                if (!table.StartsWith("lock:", StringComparison.Ordinal))
                    report.Deleted.Add((table, rows));
            }

            await db.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE audit_log ENABLE TRIGGER audit_log_no_delete",
                transaction: transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);

            // Le rollback annule le DELETE mais PAS le DISABLE TRIGGER dans toutes les versions de
            // Postgres ; on le rétablit explicitement plutôt que de le supposer. Laisser le journal
            // d'audit réécrivable après un échec serait un dommage bien pire que la purge ratée.
            await db.ExecuteAsync(new CommandDefinition(
                "ALTER TABLE audit_log ENABLE TRIGGER audit_log_no_delete", cancellationToken: ct));
            throw;
        }

        // Tracé côté service, pas dans `audit_log` : y écrire l'effacement d'une organisation
        // reviendrait à conserver son identifiant dans la table même qu'on vient de purger.
        _logger.Warning(
            "Organisation {OrgId} définitivement effacée : {Rows} lignes sur {Tables} tables ({Detail})",
            orgId, report.TotalRows, report.Deleted.Count,
            string.Join(", ", report.Deleted.Where(d => d.Rows > 0).Select(d => $"{d.Table}={d.Rows}")));

        return report;
    }
}
