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

## 6. Restauration à un instant précis (PITR)

Le dump logique de §2 ramène l'état du dump. C'est le bon outil pour « la machine a brûlé », et le
mauvais pour « quelqu'un a lancé la mauvaise commande à 14 h 32 » : un dump quotidien signifie
jusqu'à 24 h de perte, et ne sait pas viser un instant. La restauration à un instant précis répond à
la seconde question — la perte se mesure en secondes, et l'on choisit le moment.

**Les deux se gardent.** Le dump logique est le seul qui survive à un changement de version majeure
de PostgreSQL, se restaure table par table, et se relit sans la grappe d'origine. La sauvegarde
physique est liée à la version et à l'architecture qui l'a produite. L'une n'est pas une version
améliorée de l'autre.

### 6.1 Activer l'archivage WAL

Sans archivage, il n'y a pas de PITR : le journal qui permettrait de rejouer est recyclé au fil de
l'eau. C'est une configuration serveur, à poser **avant** la première sauvegarde de base.

Avec Docker, l'overlay fourni suffit :

```bash
docker compose -f docker-compose.yml -f docker-compose.pitr.yml up -d
```

Sur une installation par paquet, dans `conf.d/pitr.conf` :

```conf
wal_level = replica
archive_mode = on
archive_command = 'test ! -f /var/backups/agenthost/wal/%f && cp %p /var/backups/agenthost/wal/%f'
archive_timeout = 300
```

Trois détails valent d'être connus :

- **`test ! -f` d'abord.** PostgreSQL peut rappeler la commande pour un segment déjà archivé.
  L'écraser corromprait l'archive à l'endroit précis où l'on en aurait besoin ; la commande doit
  refuser, pas réussir à moitié.
- **`archive_timeout`.** Sans lui, une instance peu active garde son segment courant ouvert
  indéfiniment : l'archive paraît à jour et la fenêtre de perte réelle est sans limite.
- **L'archive va sur un autre stockage que la base.** Une archive rangée à côté des données
  disparaît avec elles. Elle n'a de valeur que si elle survit à ce dont elle doit permettre la
  reprise.

`archive_mode` demande un redémarrage du serveur.

### 6.2 Sauvegarde de base

```bash
scripts/basebackup.sh /var/backups/agenthost/base
```

À planifier plus rarement que le dump logique (hebdomadaire suffit souvent) : c'est le point de
départ du rejeu, et les segments WAL couvrent l'intervalle. Plus la sauvegarde est ancienne, plus la
restauration rejoue longtemps — c'est le seul arbitrage.

Le script avertit si `archive_mode` est inactif : une sauvegarde de base sans archive est
restaurable telle quelle, mais ne permet **pas** de PITR, et il vaut mieux l'apprendre le jour de la
sauvegarde que le jour de l'incident.

### 6.3 Restaurer

```bash
sudo -u postgres scripts/restore-pitr.sh \
    /var/backups/agenthost/base/20260810T052032Z \
    /var/backups/agenthost/wal \
    '2026-08-10 14:32:00+00'
```

**La grappe d'origine n'est jamais touchée.** La restauration se fait dans un répertoire neuf et sur
un port distinct (5433 par défaut) : on obtient une seconde instance, à côté, qu'on interroge avant
de décider quoi que ce soit. Une procédure qui écrase la production pour être vérifiée n'est pas une
procédure, c'est un second incident.

Points d'attention :

- **Précisez le fuseau dans l'horodatage.** Un horodatage nu est interprété dans le fuseau du
  serveur, et se tromper d'une heure pendant une restauration d'urgence ne se remarque qu'après.
- **La reprise s'arrête *avant* la première transaction qui dépasse la cible.** Le script affiche
  l'instant réellement atteint ; il diffère toujours un peu de celui demandé.
- **À exécuter sous le compte propriétaire de PostgreSQL.** Le serveur refuse de tourner en root, et
  la commande de restauration doit pouvoir lire l'archive.
- **Sur Debian et Ubuntu, la sauvegarde n'emporte pas la configuration** : la distribution range
  `postgresql.conf` et `pg_hba.conf` hors du répertoire de données, que `pg_basebackup` est seul à
  copier. Le script en génère donc une minimale, en lisant dans le fichier de contrôle de la
  sauvegarde les quatre réglages de dimensionnement que la reprise exige d'égaler. Cette
  configuration ouvre la sauvegarde ; elle ne remplace pas celle de production avant une bascule.

**Vérifié de bout en bout** contre une vraie grappe PostgreSQL 16 : sauvegarde de base, écriture,
suppression accidentelle d'une ligne, restauration à un instant antérieur. L'instance restaurée
portait bien la ligne supprimée et ignorait l'écriture postérieure à la cible, pendant que la
production restait inchangée.

### 6.4 Nettoyage de l'archive

Les segments antérieurs à la plus ancienne sauvegarde de base conservée ne servent plus à rien, et
une archive qui grossit sans limite finit par remplir le disque — c'est-à-dire par arrêter la base
qu'elle devait protéger. `pg_archivecleanup` fait ce ménage :

Chaque sauvegarde de base dépose dans l'archive un marqueur `<segment>.<décalage>.backup`. Celui de
la plus ancienne sauvegarde que l'on conserve donne la borne : tout ce qui le précède est
inutilisable, puisque plus aucune sauvegarde ne permet de rejouer à partir de là.

```bash
# Purger d'abord les vieilles sauvegardes de base (basebackup.sh le fait), puis :
OLDEST=$(ls -1 /var/backups/agenthost/wal/*.backup | sort | head -1)

pg_archivecleanup -n /var/backups/agenthost/wal "$(basename "$OLDEST")"   # ce qui serait supprimé
pg_archivecleanup    /var/backups/agenthost/wal "$(basename "$OLDEST")"   # suppression
```

**Toujours `-n` d'abord.** La commande supprime tout ce qui précède le marqueur, sans confirmation :
un marqueur trop récent efface la seule fenêtre de rejeu qui restait.

Surveillez l'espace disque de l'archive : une archive qui grossit sans limite finit par remplir le
volume, c'est-à-dire par arrêter la base qu'elle devait protéger. Aucune métrique `agenthost.` ne
couvre cela — c'est à la supervision système de le porter.

---

## 7. Ce qui n'est pas couvert

- **Pas de réplication ni de bascule automatique.** La base reste un point de défaillance unique.
  L'archivage mis en place ci-dessus est la moitié du chemin : une réplique en flux se monte à
  partir des mêmes éléments — `pg_basebackup --write-recovery-conf` sur le secondaire, un
  `primary_conninfo` vers le primaire, et le même `restore_command` en secours si le flux
  décroche. Ce qui manque n'est pas la sauvegarde mais la **bascule** : détection de panne, adresse
  virtuelle ou proxy, protection contre le double primaire. Cela relève d'un gestionnaire de grappe
  (Patroni, repmgr) et du déploiement, pas de ce dépôt.
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
réponse à une demande d’effacement (voir §7).

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
