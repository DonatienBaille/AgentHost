#!/usr/bin/env bash
#
# Restauration à un instant précis (PITR) d'Agent Host.
#
# Rejoue une sauvegarde de base (`scripts/basebackup.sh`) puis les segments WAL archivés, en
# s'arrêtant à l'instant demandé. C'est ce que `restore.sh` ne sait pas faire : un dump logique
# ramène l'état du dump, celui-ci ramène l'état d'avant la commande qui a tout cassé.
#
# LA GRAPPE D'ORIGINE N'EST JAMAIS TOUCHÉE. La restauration se fait dans un répertoire neuf et sur
# un port distinct : on obtient une seconde instance, à côté, qu'on inspecte avant de décider quoi
# que ce soit. Une procédure de restauration qui écrase la production pour être vérifiée n'est pas
# une procédure, c'est un second incident.
#
# Usage :
#   scripts/restore-pitr.sh <sauvegarde_de_base> <répertoire_wal> <horodatage> [répertoire_cible]
#
# Exemple :
#   scripts/restore-pitr.sh backups/base/20260810T120000Z /var/backups/agenthost/wal \
#       '2026-08-10 14:32:00+00' /tmp/pitr
#
# L'horodatage est celui que PostgreSQL comprend (`recovery_target_time`). Précisez le fuseau : un
# horodatage nu est interprété dans le fuseau du serveur, et se tromper d'une heure lors d'une
# restauration d'urgence est le genre d'erreur qu'on ne remarque qu'après.
#
# Variables :
#   PITR_PORT   port de l'instance restaurée (défaut 5433 — surtout pas celui de la production)

set -euo pipefail

BASE_BACKUP="${1:-}"
WAL_ARCHIVE="${2:-}"
TARGET_TIME="${3:-}"
RESTORE_DIR="${4:-./pitr-restore}"
PITR_PORT="${PITR_PORT:-5433}"

if [ -z "$BASE_BACKUP" ] || [ -z "$WAL_ARCHIVE" ] || [ -z "$TARGET_TIME" ]; then
    sed -n '2,30p' "$0" >&2
    exit 2
fi

[ -f "$BASE_BACKUP/base.tar.gz" ] || { echo "Sauvegarde de base introuvable : $BASE_BACKUP/base.tar.gz" >&2; exit 2; }
[ -d "$WAL_ARCHIVE" ] || { echo "Archive WAL introuvable : $WAL_ARCHIVE" >&2; exit 2; }

# Sur Debian et Ubuntu — donc sur la cible de déploiement — les binaires du serveur ne sont PAS dans
# le PATH : la distribution n'expose que ses enveloppes (`pg_ctlcluster`), qui refusent de piloter
# un répertoire de données arbitraire. Il faut donc aller chercher `pg_ctl` là où il est réellement,
# sans quoi ce script échoue au pire moment sur la plateforme la plus courante.
if command -v pg_ctl >/dev/null 2>&1; then
    PG_CTL="$(command -v pg_ctl)"
elif command -v pg_config >/dev/null 2>&1 && [ -x "$(pg_config --bindir)/pg_ctl" ]; then
    PG_CTL="$(pg_config --bindir)/pg_ctl"
else
    # Plusieurs versions peuvent cohabiter ; on prend la plus récente, qui est celle capable de lire
    # les sauvegardes des précédentes.
    PG_CTL="$(ls -d /usr/lib/postgresql/*/bin/pg_ctl 2>/dev/null | sort -V | tail -1 || true)"
fi

[ -n "${PG_CTL:-}" ] && [ -x "$PG_CTL" ] || {
    echo "pg_ctl introuvable. Installez le paquet serveur PostgreSQL, ou ajoutez son répertoire" >&2
    echo "bin au PATH (Debian/Ubuntu : /usr/lib/postgresql/<version>/bin)." >&2
    exit 1
}

# La restauration démarre un serveur : PostgreSQL refuse catégoriquement de tourner en root, et
# `restore_command` doit pouvoir lire l'archive WAL, dont les droits appartiennent au même
# utilisateur. Le dire ici plutôt que de laisser `pg_ctl` échouer dix lignes plus bas.
if [ "$(id -u)" -eq 0 ]; then
    echo "ERREUR : à exécuter sous le compte propriétaire de PostgreSQL, pas en root." >&2
    echo "         Exemple : sudo -u postgres $0 $*" >&2
    exit 2
fi

# Un répertoire cible non vide serait ambigu : on ne saurait plus ce qui vient de la sauvegarde et
# ce qui traînait là. Refuser est préférable à écraser.
if [ -e "$RESTORE_DIR" ] && [ -n "$(ls -A "$RESTORE_DIR" 2>/dev/null)" ]; then
    echo "ERREUR : $RESTORE_DIR existe et n'est pas vide." >&2
    exit 2
fi

WAL_ARCHIVE="$(cd "$WAL_ARCHIVE" && pwd)"
mkdir -p "$RESTORE_DIR"
RESTORE_DIR="$(cd "$RESTORE_DIR" && pwd)"

echo "Déballage de la sauvegarde de base dans $RESTORE_DIR"
tar -xzf "$BASE_BACKUP/base.tar.gz" -C "$RESTORE_DIR"

# `pg_wal.tar.gz` porte les segments produits pendant la sauvegarde (option -X stream). Ils sont
# nécessaires pour rendre la sauvegarde cohérente AVANT même de commencer à rejouer l'archive.
if [ -f "$BASE_BACKUP/pg_wal.tar.gz" ]; then
    mkdir -p "$RESTORE_DIR/pg_wal"
    tar -xzf "$BASE_BACKUP/pg_wal.tar.gz" -C "$RESTORE_DIR/pg_wal"
fi

chmod 700 "$RESTORE_DIR"

# SUR DEBIAN ET UBUNTU, LA SAUVEGARDE N'EMPORTE PAS LA CONFIGURATION. La distribution range
# `postgresql.conf` et `pg_hba.conf` sous /etc/postgresql/<version>/<grappe>/, donc hors du
# répertoire de données — et `pg_basebackup` ne copie que le répertoire de données. Une restauration
# physique arrive donc sans configuration du tout, et le serveur refuse de démarrer. Ce n'est pas
# une lacune de la sauvegarde : c'est une propriété de l'empaquetage, qu'une procédure de
# restauration doit connaître plutôt que découvrir pendant un incident.
if [ ! -f "$RESTORE_DIR/postgresql.conf" ]; then
    echo "Configuration absente de la sauvegarde (empaquetage Debian) : génération d'une configuration minimale"

    # Ces quatre réglages ne peuvent pas être choisis au hasard : la reprise refuse de démarrer si
    # l'instance restaurée en offre MOINS que celle qui a produit les journaux. Les valeurs se lisent
    # dans le fichier de contrôle, c'est-à-dire dans la sauvegarde elle-même — la seule source qui
    # décrive la grappe d'origine sans avoir à y accéder.
    # `pg_controldata` nomme deux de ces réglages autrement que les paramètres correspondants
    # (`max_prepared_xacts`, `max_locks_per_xact`) : lire l'étiquette de sortie et non le nom du
    # paramètre. Une valeur vide produirait un fichier de configuration syntaxiquement invalide, et
    # le serveur refuserait de démarrer — d'où le contrôle explicite plus bas.
    PG_CONTROLDATA="$(dirname "$PG_CTL")/pg_controldata"
    control() {
        local value
        value="$("$PG_CONTROLDATA" "$RESTORE_DIR" | sed -n "s/^$1 setting: *//p" | tr -d '[:space:]')"
        [ -n "$value" ] || { echo "ERREUR : pg_controldata ne rend pas '$1 setting'." >&2; exit 1; }
        printf '%s' "$value"
    }

    cat > "$RESTORE_DIR/postgresql.conf" <<EOF
# Configuration minimale écrite par scripts/restore-pitr.sh pour une instance d'inspection.
# Ce n'est PAS la configuration de production : reprenez celle de /etc/postgresql/ avant toute
# bascule. Elle suffit à ouvrir la sauvegarde et à la relire.
listen_addresses = 'localhost'
max_connections = $(control max_connections)
max_worker_processes = $(control max_worker_processes)
max_wal_senders = $(control max_wal_senders)
max_prepared_transactions = $(control max_prepared_xacts)
max_locks_per_transaction = $(control max_locks_per_xact)
EOF
fi

if [ ! -f "$RESTORE_DIR/pg_hba.conf" ]; then
    # Les rôles et leurs mots de passe viennent de la sauvegarde — c'est une copie physique. On
    # exige donc une vraie authentification plutôt que `trust` : une instance de restauration
    # contient exactement les mêmes données que la production, et n'a aucune raison d'être plus
    # ouverte qu'elle.
    cat > "$RESTORE_DIR/pg_hba.conf" <<'EOF'
local   all   all                  peer
host    all   all   127.0.0.1/32   scram-sha-256
host    all   all   ::1/128        scram-sha-256
EOF
fi

# Vide, mais présent : son absence n'empêche pas le démarrage, elle produit seulement une ligne
# d'erreur dans le journal de la reprise. Or c'est ce journal qu'on lit pour juger si la
# restauration s'est bien passée, et une erreur sans conséquence y est un bruit qui coûte cher au
# mauvais moment.
[ -f "$RESTORE_DIR/pg_ident.conf" ] || : > "$RESTORE_DIR/pg_ident.conf"

cat >> "$RESTORE_DIR/postgresql.auto.conf" <<EOF

# --- Ajouté par scripts/restore-pitr.sh ---
restore_command = 'cp $WAL_ARCHIVE/%f %p'
recovery_target_time = '$TARGET_TIME'

# 'promote' : une fois l'instant atteint, l'instance s'ouvre en écriture et la restauration est
# terminée. 'pause' (le défaut) la laisse en attente d'une décision manuelle, ce qui est le bon
# choix en exploration interactive mais fait pendre un script.
recovery_target_action = 'promote'

# Sans cette borne, PostgreSQL suivrait la dernière ligne temporelle de l'archive — c'est-à-dire,
# après une première restauration, celle qu'on vient de créer. On veut rejouer l'historique
# d'origine.
recovery_target_timeline = 'current'

port = $PITR_PORT
archive_mode = off
EOF

# Le fichier qui déclenche la restauration. Sans lui, PostgreSQL démarre normalement et ignore
# purement et simplement les directives ci-dessus — la restauration paraîtrait réussie et rendrait
# l'état de la sauvegarde, pas celui demandé.
touch "$RESTORE_DIR/recovery.signal"

LOG="$RESTORE_DIR/pitr.log"
echo "Démarrage de l'instance restaurée sur le port $PITR_PORT (cible : $TARGET_TIME)"
"$PG_CTL" --pgdata="$RESTORE_DIR" --log="$LOG" --wait --timeout=300 start || {
    echo "ERREUR : l'instance n'a pas démarré. Journal :" >&2
    tail -40 "$LOG" >&2
    exit 1
}

# `pg_ctl start --wait` rend la main dès que le serveur accepte des connexions — ce qui arrive
# EN LECTURE SEULE, pendant la reprise, avant la promotion. Conclure là serait conclure trop tôt :
# on tiendrait pour restaurée une instance encore en train de rejouer. La seule question qui
# tranche est posée au serveur lui-même.
PSQL="$(dirname "$PG_CTL")/psql"
echo -n "Attente de la fin de la reprise"
for _ in $(seq 1 300); do
    if [ "$("$PSQL" -h /var/run/postgresql -p "$PITR_PORT" -d postgres -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null)" = "f" ]; then
        RECOVERY_DONE=1
        break
    fi
    echo -n "."
    sleep 1
done
echo

if [ -z "${RECOVERY_DONE:-}" ]; then
    echo "ERREUR : la reprise ne s'est pas achevée. L'instance sert peut-être un état antérieur" >&2
    echo "         à la cible. Causes usuelles : segment WAL manquant dans l'archive, ou cible" >&2
    echo "         postérieure au dernier journal archivé. Journal :" >&2
    tail -40 "$LOG" >&2
    exit 1
fi

# L'instant réellement atteint, et non celui demandé. Les deux diffèrent toujours un peu — la reprise
# s'arrête AVANT la première transaction qui dépasse la cible — et l'écart est ce qui dit si la
# restauration a ramené ce qu'on croyait. Le taire laisserait l'opérateur le supposer.
grep -E "last completed transaction was at log time|recovery stopping" "$LOG" | tail -2 || true

echo
echo "Instance restaurée et ouverte en écriture :"
echo "  psql -h localhost -p $PITR_PORT -U ${PGUSER:-agenthost} agenthost"
echo "  arrêt : $PG_CTL --pgdata=$RESTORE_DIR stop"
echo
echo "VÉRIFIEZ AVANT DE BASCULER. La restauration s'arrête à l'instant demandé, pas à l'instant"
echo "correct : si la cible a été mal choisie, l'instance est cohérente et fausse à la fois."
