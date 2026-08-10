#!/usr/bin/env bash
#
# Sauvegarde de base physique (PITR) de la grappe PostgreSQL d'Agent Host.
#
# CE QUE CETTE SAUVEGARDE AJOUTE À `backup.sh`, ET POURQUOI ELLE NE LA REMPLACE PAS.
#
# `backup.sh` produit un dump logique : une photographie cohérente de la base à l'instant où il
# s'exécute. Quotidien, il signifie donc jusqu'à 24 h de perte — et un incident survient rarement
# juste après un dump. Un dump ne sait pas non plus revenir à un instant choisi : il ramène l'état
# du dump, pas celui d'« avant la commande qui a tout cassé ».
#
# La sauvegarde physique répond à l'autre besoin : accompagnée de l'archivage WAL, elle permet de
# rejouer la base jusqu'à n'importe quel instant compris entre la sauvegarde et le dernier segment
# archivé. La perte se mesure alors en secondes, et surtout on peut viser l'instant qui précède
# l'erreur.
#
# Les deux se gardent. Le dump logique reste le seul qui survive à un changement de version majeure
# de PostgreSQL, se restaure table par table, et se relit sans la grappe d'origine. La sauvegarde
# physique, elle, est liée à la version et à l'architecture qui l'a produite.
#
# PRÉREQUIS — l'archivage WAL doit être actif côté serveur (voir docs/operations.md §6) :
#
#   wal_level = replica
#   archive_mode = on
#   archive_command = 'test ! -f /var/backups/agenthost/wal/%f && cp %p /var/backups/agenthost/wal/%f'
#   archive_timeout = 300
#
# Sans archivage, ce script produit une sauvegarde restaurable telle quelle mais SANS PITR : on
# retombe sur une photographie, au prix d'un fichier bien plus gros qu'un dump. Le script le dit.
#
# Usage :
#   scripts/basebackup.sh [répertoire_de_sortie]
#
# Variables :
#   PGHOST PGPORT PGUSER PGPASSWORD   paramètres de connexion (rôle avec REPLICATION requis)
#   BASEBACKUP_RETENTION_DAYS         purge des sauvegardes plus vieilles que N jours (0 = jamais)

set -euo pipefail

OUTPUT_DIR="${1:-./backups/base}"
export PGHOST="${PGHOST:-localhost}"
export PGPORT="${PGPORT:-5432}"
export PGUSER="${PGUSER:-agenthost}"
RETENTION_DAYS="${BASEBACKUP_RETENTION_DAYS:-14}"

command -v pg_basebackup >/dev/null || { echo "pg_basebackup introuvable (paquet postgresql-client)" >&2; exit 1; }

# L'archivage est une propriété du serveur, pas du script : le vérifier ici évite de découvrir des
# semaines plus tard qu'on accumulait des sauvegardes sans le journal qui les rend utiles.
ARCHIVE_MODE="$(psql -tAc 'SHOW archive_mode' 2>/dev/null || echo inconnu)"
if [ "$ARCHIVE_MODE" != "on" ] && [ "$ARCHIVE_MODE" != "always" ]; then
    echo "AVERTISSEMENT : archive_mode = $ARCHIVE_MODE — cette sauvegarde ne permettra PAS de PITR." >&2
    echo "                Voir docs/operations.md §6 pour activer l'archivage WAL." >&2
fi

STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
TARGET="$OUTPUT_DIR/$STAMP"
mkdir -p "$TARGET.partial"

echo "Sauvegarde de base de $PGHOST:$PGPORT vers $TARGET"

# -X stream : les segments WAL produits PENDANT la sauvegarde voyagent avec elle, par une seconde
# connexion. Sans cela, une sauvegarde n'est restaurable que si l'archive contient déjà ces
# segments — condition vraie la plupart du temps, et fausse exactement le jour où l'archivage a
# hoqueté. Une sauvegarde qui dépend d'une autre pour être cohérente n'est pas une sauvegarde.
#
# -c fast : force un point de reprise immédiat plutôt que d'attendre le rythme naturel du serveur.
# Sans lui, `pg_basebackup` peut rester plusieurs minutes à ne rien faire.
pg_basebackup \
    --pgdata="$TARGET.partial" \
    --format=tar --gzip --compress=6 \
    --wal-method=stream \
    --checkpoint=fast \
    --progress --no-password

mv "$TARGET.partial" "$TARGET"

# Relecture : prouve que l'archive est exploitable, pas seulement présente. Une sauvegarde qu'on ne
# vérifie qu'au moment de l'incident n'est pas une sauvegarde.
if ! tar -tzf "$TARGET/base.tar.gz" >/dev/null 2>&1; then
    echo "ERREUR : $TARGET/base.tar.gz est illisible" >&2
    exit 1
fi

echo "Terminé : $(du -sh "$TARGET" | cut -f1) dans $TARGET"
echo "Restauration à un instant précis : scripts/restore-pitr.sh $TARGET <répertoire_wal> '<horodatage>'"

if [ "$RETENTION_DAYS" -gt 0 ]; then
    # Les sauvegardes de base sont volumineuses ; la rétention est plus courte que celle des dumps
    # logiques, et les segments WAL antérieurs à la plus ancienne sauvegarde conservée ne servent
    # plus à rien (voir la note sur le nettoyage de l'archive dans docs/operations.md §6).
    find "$OUTPUT_DIR" -mindepth 1 -maxdepth 1 -type d -mtime "+$RETENTION_DAYS" -print -exec rm -rf {} + || true
fi
