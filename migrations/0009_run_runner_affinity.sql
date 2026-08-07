-- Agent Host — l'affinité run → runner devient explicite et persistée (feuille de route, lot 1.1)
--
-- LE PROBLÈME. Jusqu'ici, « où tourne le conteneur de ce run » n'était écrit nulle part : c'était
-- une propriété implicite du processus qui avait reçu la requête de création, puisque ce même
-- processus parlait au démon de son propre nœud. `StopAsync(runId)` et `GetLogsAsync(runId)`
-- listaient les conteneurs portant l'étiquette `agenthost.run_id` — sur CE démon-là. Une seconde
-- réplique du backend n'y trouvait rien, et n'en tirait aucune conclusion : elle répondait
-- « annulé » sans rien avoir arrêté. C'est cette absence de colonne, et non Docker, qui imposait
-- `replicaCount: 1`, le HPA désactivé et la stratégie `Recreate`.
--
-- POURQUOI `runner_url` ET NON `runner_id`. Les deux ont été pesés.
--
--   * `runner_id` suppose un annuaire : une table de runners enregistrés, avec leur adresse, leur
--     état de santé et un battement de cœur pour détecter les disparus. C'est un composant de plus
--     — écritures concurrentes, entrées périmées, nettoyage — dont la seule fonction serait de
--     retrouver une adresse que l'on connaissait déjà au moment du lancement.
--
--   * `runner_url` rend la ligne autosuffisante : elle porte de quoi joindre le détenteur du run
--     sans consulter quoi que ce soit d'autre. En DaemonSet, l'adresse à composer est justement
--     l'IP du pod runner du nœud — il n'existe pas de Service par nœud vers lequel router.
--
-- Le reproche que l'on peut faire à l'URL est réel et il est traité, pas ignoré : une IP de pod
-- n'est pas stable, et elle peut être RÉATTRIBUÉE à un pod d'un autre nœud. Une URL enregistrée
-- peut donc pointer vers un runner qui n'a jamais eu ce run. C'est pourquoi le protocole du runner
-- répond explicitement « unknown_run » (404 sur /wait, `confirmed: false` sur /stop) lorsqu'il ne
-- connaît ni le run ni de conteneur portant son étiquette. Le backend voit alors une annulation non
-- confirmée — et le dit — au lieu de prendre le silence d'un runner étranger pour un succès.
--
-- NULLABLE, ET ÇA COMPTE. Trois populations de runs ont légitimement la colonne à NULL :
--   1. les runs antérieurs à cette migration ;
--   2. tous les runs lancés en mode `Runner:Mode = inprocess` (le défaut), où il n'y a pas de tier
--      runner et où l'orchestrateur en processus reste seul compétent ;
--   3. les runs qui n'ont jamais atteint le lancement (échec en Provisioning/Preparing).
-- Le backend ne doit donc jamais traiter NULL comme une erreur, mais comme « aucun runner
-- enregistré » : en mode distant, l'annulation et les journaux répondent explicitement qu'ils ne
-- savent pas où chercher, plutôt que de prétendre avoir agi.
--
-- Écrite AVANT le lancement, pas après : un conteneur démarré dont aucune ligne ne dit où il est
-- n'est plus rattrapable, alors qu'une ligne qui désigne un runner sans conteneur est inoffensive
-- (l'arrêt n'y trouve rien et le confirme).

ALTER TABLE runs ADD COLUMN IF NOT EXISTS runner_url VARCHAR(500);

COMMENT ON COLUMN runs.runner_url IS
    'Base URL du tier runner qui détient le conteneur de ce run (mode Runner:Mode=remote). '
    'NULL = aucun runner enregistré : run antérieur au tier runner, lancé en mode inprocess, ou '
    'jamais parvenu au lancement. Écrite avant le lancement ; sert à router stop et logs.';

-- Index partiel : les seules lectures qui balayent cette colonne cherchent les runs encore vivants
-- d'un runner donné (exploitation : « qu'est-ce que ce nœud détenait avant de disparaître ? »).
-- Partiel sur NOT NULL parce qu'en mode inprocess la colonne est nulle partout, et qu'un index
-- plein n'y serait qu'une écriture supplémentaire à chaque run.
CREATE INDEX IF NOT EXISTS idx_runs_runner_url ON runs(runner_url) WHERE runner_url IS NOT NULL;
