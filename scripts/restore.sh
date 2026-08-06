#!/usr/bin/env bash
#
# Restauration d'une sauvegarde Agent Host.
#
# Restaure dans une base **neuve** par défaut, jamais par-dessus une base existante : écraser une
# production sur un malentendu est irréversible, et le seul garde-fou fiable est de rendre le geste
# explicite. Pour restaurer dans une base occupée, il faut passer --force.
#
# Usage :
#   scripts/restore.sh <fichier.dump> [nom_base_cible]
#   scripts/restore.sh <fichier.dump> agenthost --force     # écrase une base existante
#
# APRÈS LA RESTAURATION, deux choses ne sont pas dans le dump et doivent correspondre :
#
#   * Secrets:EncryptionKey — les colonnes chiffrées (secrets, seeds TOTP) ne se déchiffrent
#     qu'avec la clé en usage au moment du dump. Une clé différente ne provoque aucune erreur au
#     démarrage : les secrets échouent silencieusement à la première utilisation par un agent, et
#     les comptes MFA se retrouvent verrouillés. Si vous restaurez avec une clé différente, mettez
#     l'ancienne dans Secrets:PreviousEncryptionKeys et lancez `--rekey-secrets` ;
#   * Jwt:Secret — une valeur différente invalide tous les jetons en circulation. Sans gravité,
#     mais tout le monde est déconnecté.
#
# Les migrations sont idempotentes et rejouées au démarrage de l'API : il n'y a rien à faire de ce
# côté après une restauration.

set -euo pipefail

DUMP_FILE="${1:-}"
TARGET_DB="${2:-agenthost_restored}"
FORCE="${3:-}"

export PGHOST="${PGHOST:-localhost}"
export PGPORT="${PGPORT:-5432}"
export PGUSER="${PGUSER:-agenthost}"

if [ -z "$DUMP_FILE" ]; then
    echo "Usage : scripts/restore.sh <fichier.dump> [nom_base_cible] [--force]" >&2
    exit 1
fi
[ -f "$DUMP_FILE" ] || { echo "Fichier introuvable : $DUMP_FILE" >&2; exit 1; }
command -v pg_restore >/dev/null || { echo "pg_restore introuvable (paquet postgresql-client)" >&2; exit 1; }

# Vérifier le dump AVANT de toucher à quoi que ce soit : découvrir qu'il est corrompu après avoir
# supprimé la base cible serait la pire séquence possible.
pg_restore --list "$DUMP_FILE" >/dev/null || { echo "Dump illisible : $DUMP_FILE" >&2; exit 1; }

EXISTS=$(psql -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$TARGET_DB'")

if [ -n "$EXISTS" ]; then
    if [ "$FORCE" != "--force" ]; then
        cat >&2 <<EOF
La base « $TARGET_DB » existe déjà.

Restaurez plutôt dans une base neuve, puis basculez la chaîne de connexion :
    scripts/restore.sh "$DUMP_FILE" agenthost_restore_$(date -u +%Y%m%d)

Pour écraser « $TARGET_DB » — destructif, sans retour possible :
    scripts/restore.sh "$DUMP_FILE" "$TARGET_DB" --force
EOF
        exit 1
    fi
    echo "ATTENTION : suppression de « $TARGET_DB » dans 5 s (Ctrl-C pour annuler)"
    sleep 5
    dropdb "$TARGET_DB"
fi

createdb "$TARGET_DB"
echo "Restauration de $DUMP_FILE vers $TARGET_DB"

# --exit-on-error : une restauration à moitié faite qui rend 0 est pire qu'un échec franc.
pg_restore --dbname="$TARGET_DB" --no-owner --no-privileges --exit-on-error "$DUMP_FILE"

ORGS=$(psql -d "$TARGET_DB" -tAc "SELECT count(*) FROM organizations" 2>/dev/null || echo '?')
RUNS=$(psql -d "$TARGET_DB" -tAc "SELECT count(*) FROM runs" 2>/dev/null || echo '?')
SECRETS=$(psql -d "$TARGET_DB" -tAc "SELECT count(*) FROM secrets WHERE encrypted_value IS NOT NULL" 2>/dev/null || echo '?')

cat <<EOF

Restauré dans « $TARGET_DB » : $ORGS organisation(s), $RUNS run(s), $SECRETS secret(s) chiffré(s).

À vérifier maintenant :
  1. Secrets:EncryptionKey correspond bien à celle en usage au moment du dump — sinon les
     $SECRETS secret(s) et tous les seeds TOTP sont illisibles, en silence.
  2. Pointez ConnectionStrings__DefaultConnection sur « $TARGET_DB » et démarrez l'API : les
     migrations idempotentes se rejouent seules.
  3. Test de bout en bout : connexion, puis un run qui consomme un secret. C'est le seul moyen de
     savoir que la clé est la bonne.
EOF
