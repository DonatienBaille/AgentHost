# Exploitation d'Agent Host

Sauvegarde, restauration, rotation de la clé de chiffrement, et immuabilité du journal d'audit.

---

## 1. Ce qui doit être sauvegardé, et où

Quatre choses portent l'état d'une instance. Les traiter comme un seul bloc est l'erreur qui rend
une restauration inutilisable.

| Élément | Contenu | Sauvegarde |
| --- | --- | --- |
| **Base PostgreSQL** | Tout le modèle métier, y compris les colonnes chiffrées | `scripts/backup.sh` |
| **`Secrets:EncryptionKey`** | La clé AES-256 qui déchiffre les secrets et les seeds TOTP | **Coffre distinct de celui des dumps** |
| **`Jwt:Secret`** | La clé de signature des jetons | Coffre |
| **Artefacts** | Fichiers produits par les runs (disque local ou S3) | Sauvegarde du volume ou du bucket |

La règle qui compte : **la clé de chiffrement ne va pas au même endroit que les dumps**. Les stocker
ensemble revient à ne pas chiffrer — quiconque obtient la sauvegarde obtient tout. Et l'inverse est
tout aussi fatal : un dump sans sa clé se restaure sans la moindre erreur, puis les secrets échouent
silencieusement à la première utilisation par un agent, et les comptes à second facteur sont
verrouillés.

## 2. Sauvegarde

```bash
PGPASSWORD=... scripts/backup.sh /var/backups/agenthost
```

Format personnalisé `pg_dump -Fc` : compressé, restaurable sélectivement, et inspectable par
`pg_restore --list` sans être restauré. Le script écrit dans un fichier `.partial` puis le renomme,
de sorte qu'un dump interrompu ne porte jamais un nom qui le fait passer pour complet, et relit
l'en-tête après écriture pour prouver que le fichier est exploitable et pas seulement non vide.

`BACKUP_RETENTION_DAYS` (30 par défaut) purge les dumps plus anciens ; `0` désactive la purge.

En cron, quotidiennement :

```cron
0 3 * * * PGPASSWORD=... /opt/agenthost/scripts/backup.sh /var/backups/agenthost >> /var/log/agenthost-backup.log 2>&1
```

## 3. Restauration

```bash
# Par défaut : dans une base NEUVE, puis on bascule la chaîne de connexion.
PGPASSWORD=... scripts/restore.sh /var/backups/agenthost/agenthost-20260806T030000Z.dump

# Écraser une base existante — destructif, il faut le demander explicitement.
PGPASSWORD=... scripts/restore.sh <dump> agenthost --force
```

Le script vérifie le dump **avant** de toucher à la base cible : découvrir qu'il est corrompu après
avoir supprimé la base serait la pire séquence possible. Il refuse d'écraser une base existante sans
`--force` et sort en code 1.

Après restauration :

1. `Secrets:EncryptionKey` doit être celle en usage au moment du dump. Si ce n'est pas le cas, mettez
   l'ancienne dans `Secrets:PreviousEncryptionKeys` et lancez le rechiffrement (§4).
2. Pointez `ConnectionStrings__DefaultConnection` sur la base restaurée et démarrez l'API — les
   migrations sont idempotentes et se rejouent seules.
3. Testez de bout en bout : connexion, puis un run qui consomme un secret. C'est le **seul** moyen
   de savoir que la clé est la bonne, puisqu'une mauvaise clé ne provoque aucune erreur au démarrage.

Les triggers d'immuabilité de `audit_log` (§5) sont inclus dans le dump et rétablis par la
restauration — vérifié.

## 4. Rotation de la clé de chiffrement des secrets

À faire si la clé a fuité, ou périodiquement selon votre politique. Deux colonnes sont concernées :
`secrets.encrypted_value` et `user_mfa.secret_encrypted` (les seeds TOTP). **En oublier une revient
à verrouiller tous les comptes à second facteur.**

Le déroulé est en trois temps et l'ordre n'est pas négociable.

### Étape 1 — la nouvelle clé devient courante, l'ancienne reste lisible

```jsonc
{
  "Secrets": {
    "EncryptionKey": "<NOUVELLE clé, base64 de 32 octets>",
    "PreviousEncryptionKeys": ["<ANCIENNE clé>"]
  }
}
```

Redéployez. À partir de là le service **écrit** avec la nouvelle clé et **lit** avec l'une ou
l'autre. Aucune coupure, aucun secret perdu. Générer une clé :

```bash
openssl rand -base64 32
```

### Étape 2 — rechiffrer l'existant

```bash
dotnet AgentHost.Api.dll --rekey-secrets
```

La commande réécrit tout le matériel chiffré avec la clé courante, journalise un décompte par table,
et **sort en code non nul si une seule valeur a échoué**. Elle est idempotente : on peut la relancer
après une interruption. Elle ne détruit jamais une valeur qu'elle n'a pas su déchiffrer — elle la
compte en échec et passe à la suivante.

C'est une opération d'instance, pas d'organisation : elle n'est volontairement exposée que par la
ligne de commande, pas par l'API, parce qu'il n'y a ni appelant à autoriser ni tenant à scoper.

Un échec signifie presque toujours qu'il manque une ancienne clé dans `PreviousEncryptionKeys`.
**Ne passez pas à l'étape 3 tant que le compte d'échecs n'est pas nul.**

### Étape 3 — retirer l'ancienne clé

```jsonc
{ "Secrets": { "EncryptionKey": "<NOUVELLE clé>", "PreviousEncryptionKeys": [] } }
```

C'est cette étape qui rend la clé fuitée réellement inutile. Tant qu'elle figure dans la
configuration, la rotation n'a rien réglé.

> Les sauvegardes antérieures à la rotation restent chiffrées avec l'ancienne clé. Conservez-la dans
> votre coffre aussi longtemps que vous conservez ces dumps, sans quoi ils deviennent
> irrécupérables.

## 5. Immuabilité du journal d'audit

`audit_log` est append-only depuis la migration `0008` : trois triggers refusent `UPDATE`, `DELETE`
et `TRUNCATE`. Le `TRUNCATE` a son propre trigger de niveau instruction, parce qu'un trigger
`BEFORE DELETE FOR EACH ROW` ne le voit jamais — c'est justement le raccourci qu'emprunterait
quelqu'un voulant vider le journal d'un coup.

Une entrée erronée **ne se corrige pas** : on ajoute une entrée qui décrit la correction.

Ce que cela ne protège pas, et il faut le savoir :

- un rôle capable de `ALTER TABLE audit_log DISABLE TRIGGER` ou `DROP TRIGGER` contourne tout. Le
  propriétaire de la table en est capable, et **l'application est aujourd'hui propriétaire**. Un WORM
  réel demande une séparation de rôles : l'application écrit avec un rôle non propriétaire, les
  migrations s'exécutent avec un rôle d'administration distinct. Cette séparation n'est pas faite —
  elle relève du déploiement (docker-compose, Helm, chaînes de connexion), pas d'un fichier SQL. Le
  trigger transforme une modification silencieuse en acte délibéré, ce qui est déjà l'essentiel ;
- une restauration de sauvegarde, qui recrée la table à partir du dump.

Pour une purge réglementaire, désactivez explicitement le trigger, tracez l'opération **hors base**,
et réactivez-le.

## 6. Ce qui n'est pas couvert

- **Pas de réplication ni de bascule.** La base est un point de défaillance unique ; ces scripts
  couvrent la sauvegarde, pas la haute disponibilité.
- **Pas de restauration à un instant précis (PITR).** Il faudrait l'archivage WAL, qui se configure
  côté PostgreSQL. Un dump quotidien signifie jusqu'à 24 h de perte.
- **Pas de test de restauration automatisé.** Une sauvegarde jamais restaurée n'est pas une
  sauvegarde : restaurez périodiquement dans une base jetable et vérifiez qu'un run consommant un
  secret fonctionne.

---

## Effacement définitif d'une organisation (RGPD)

```bash
dotnet AgentHost.Api.dll --purge-org 01HZX...
```

**Ce n'est pas la suppression ordinaire.** `DELETE /api/organizations/{id}` pose un `deleted_at` :
une erreur de manipulation se rattrape, et les runs passés gardent un sens. Cette commande-ci
efface — définitivement, sans corbeille, et sans qu'aucune sauvegarde antérieure ne soit affectée.
Restaurer une sauvegarde prise avant la purge ramènerait les données ; c'est une conséquence du
mécanisme de sauvegarde, pas un défaut de la purge, et elle doit être prise en compte dans la
réponse à une demande d'effacement (voir §6).

**Codes de sortie** — pensés pour un script :

| Code | Signification |
| ---- | ------------- |
| `0`  | organisation effacée |
| `2`  | usage incorrect (identifiant manquant) |
| `3`  | l'organisation n'existait pas — rien à faire, ce n'est pas une erreur |

**Hors ligne, et pas derrière une API.** Il n'y a pas d'appelant à autoriser : celui dont on efface
l'organisation ne peut pas, par construction, rester authentifié à la fin de l'opération. Une purge
accessible par HTTP serait une suppression de compte à un jeton de distance.

**Tout ou rien.** Une seule transaction sur une vingtaine de tables. Une purge à moitié faite
laisserait des lignes orphelines que plus aucun chemin applicatif ne sait atteindre — donc des
données personnelles devenues invisibles, ce qui est pire que de ne rien avoir effacé.

**La purge tourne contre l'application vivante.** Elle verrouille les lignes parentes (`FOR
UPDATE`) avant d'effacer : un run en cours qui écrirait un événement pendant l'opération attend la
fin de la transaction, puis échoue — ce qui est exact, le run ayant disparu. Il n'est donc pas
nécessaire d'arrêter le service, mais il reste préférable de purger une organisation dont l'activité
est terminée.

**Le journal d'audit part avec le reste, et c'est la seule dérogation au WORM du dépôt.**
`audit_log` est append-only (migration 0008) parce qu'un journal effaçable ne prouve rien. La
responsabilité qu'il sert à établir s'exerce cependant *à l'intérieur d'une organisation vivante* :
une fois celle-ci effacée, conserver l'activité de ses membres n'est plus une garantie pour qui que
ce soit — c'est exactement la conservation que le droit interdit. Le trigger de suppression est donc
désactivé le temps de la transaction, pour les seules lignes de cette organisation, et rétabli même
en cas d'échec. L'opération est tracée dans le journal du service (pas dans `audit_log` : y écrire
l'effacement reviendrait à conserver l'identifiant dans la table qu'on vient de purger).

**Ce que la purge ne couvre pas.** Les fichiers hors base — workspaces et artefacts sur disque ou
dans le stockage objet — relèvent de la rétention (`Retention:*`, voir plus haut) et ne sont pas
supprimés par cette commande. Un effacement complet suppose donc de laisser la rétention faire son
travail, ou de nettoyer le préfixe correspondant à la main.
