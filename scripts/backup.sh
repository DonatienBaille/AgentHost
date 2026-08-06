#!/usr/bin/env bash
#
# Sauvegarde de la base Agent Host.
#
# Produit un dump au format personnalisé (`pg_dump -Fc`) : compressé, restaurable sélectivement, et
# lisible par `pg_restore -l` pour inspecter son contenu sans le restaurer. Un dump SQL brut serait
# plus lisible à l'œil mais interdirait la restauration partielle, qui est précisément ce qu'on veut
# lors d'un incident.
#
# CE QUE CE DUMP NE CONTIENT PAS, et c'est le point important :
#
#   * la clé `Secrets:EncryptionKey`. Les colonnes `secrets.encrypted_value` et
#     `user_mfa.secret_encrypted` sont du chiffré AES-256-GCM. Restaurer ce dump sans la clé
#     correspondante rend tous les secrets et tous les seeds TOTP définitivement illisibles — la
#     sauvegarde paraîtra pourtant complète et se restaurera sans une erreur. **Sauvegardez la clé
#     séparément**, dans un coffre distinct de celui qui héberge les dumps : les stocker ensemble
#     annule l'intérêt du chiffrement ;
#   * `Jwt:Secret`. Sans lui, tous les jetons d'accès et de rafraîchissement en circulation sont
#     invalides après restauration — déconnexion générale, sans perte de données ;
#   * les artefacts et les workspaces sur disque ou dans S3. La base ne porte que leurs métadonnées.
#
# Usage :
#   scripts/backup.sh [répertoire_de_sortie]
#
# Variables (valeurs par défaut adaptées à docker-compose.yml) :
#   PGHOST PGPORT PGUSER PGPASSWORD PGDATABASE   paramètres de connexion habituels
#   BACKUP_RETENTION_DAYS                        purge des dumps plus vieux que N jours (0 = jamais)

set -euo pipefail

OUTPUT_DIR="${1:-./backups}"
export PGHOST="${PGHOST:-localhost}"
export PGPORT="${PGPORT:-5432}"
export PGUSER="${PGUSER:-agenthost}"
export PGDATABASE="${PGDATABASE:-agenthost}"
RETENTION_DAYS="${BACKUP_RETENTION_DAYS:-30}"

command -v pg_dump >/dev/null || { echo "pg_dump introuvable (paquet postgresql-client)" >&2; exit 1; }

mkdir -p "$OUTPUT_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
TARGET="$OUTPUT_DIR/agenthost-$STAMP.dump"

echo "Sauvegarde de $PGDATABASE@$PGHOST:$PGPORT vers $TARGET"

# Écriture dans un fichier temporaire puis renommage : un dump interrompu ne doit jamais porter un
# nom qui le fait passer pour complet auprès du script de restauration ou d'un opérateur pressé.
pg_dump --format=custom --compress=9 --no-owner --no-privileges --file="$TARGET.partial"
mv "$TARGET.partial" "$TARGET"

# Relecture de l'en-tête : prouve que le fichier est un dump exploitable, pas seulement non vide.
if ! pg_restore --list "$TARGET" >/dev/null 2>&1; then
    echo "ERREUR : $TARGET n'est pas relisible par pg_restore" >&2
    exit 1
fi

TABLES=$(pg_restore --list "$TARGET" | grep -c 'TABLE DATA' || true)
echo "Terminé : $(du -h "$TARGET" | cut -f1), $TABLES table(s) de données"

if [ "$RETENTION_DAYS" -gt 0 ]; then
    find "$OUTPUT_DIR" -maxdepth 1 -name 'agenthost-*.dump' -type f -mtime "+$RETENTION_DAYS" -print -delete
fi

cat <<'RAPPEL'

RAPPEL : ce dump ne contient PAS Secrets:EncryptionKey. Sans elle, les secrets et les seeds TOTP
restaurés seront illisibles, sans le moindre message d'erreur. Sauvegardez-la ailleurs.
RAPPEL
