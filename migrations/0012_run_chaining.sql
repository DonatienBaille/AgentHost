-- Agent Host — le chaînage de runs cesse d'être à moitié modélisé (feuille de route, lot 4)
--
-- CE QUI EXISTAIT. `runs.parent_run_id` et `runs.root_run_id` sont dans le schéma depuis l'origine,
-- et `TriggeredByType` a une valeur `chain`. Rien ne les écrivait jamais : un agent qui en déclenche
-- un autre était une capacité déclarée et absente. Pire, la seule ligne de code qui touchait
-- `root_run_id` posait `root = parent`, ce qui est juste à la profondeur 1 et faux ensuite — le
-- petit-enfant aurait eu pour racine son parent, et l'arbre se serait scindé en deux.
--
-- CE QUE CETTE MIGRATION AJOUTE.
--
--   * `chain_depth` : la profondeur, écrite à la création. C'est une dénormalisation assumée. La
--     calculer à la demande demanderait de remonter la chaîne par une CTE récursive À CHAQUE
--     chaînage, uniquement pour vérifier une limite — alors que le parent connaît déjà sa propre
--     profondeur et qu'il suffit d'ajouter un. Un agent qui se chaîne lui-même est une boucle
--     infinie qui consomme le budget du projet jusqu'à épuisement ; la limite n'est donc pas un
--     raffinement, c'est le garde-fou.
--
--   * Un index sur `root_run_id` : c'est la lecture de l'arbre. Grâce à `root_run_id` porté par
--     TOUS les descendants (et pas seulement par les enfants directs), l'arbre entier se lit par
--     `WHERE root_run_id = @Root OR id = @Root` — une seule condition indexée, sans CTE récursive
--     ni aller-retour par niveau.
--
--   * Un index sur `parent_run_id` : le comptage des enfants directs, qui borne l'éventail.
--
-- LES RUNS EXISTANTS ONT UNE PROFONDEUR NULLE, et c'est exact : aucun n'a jamais été chaîné.

ALTER TABLE runs ADD COLUMN IF NOT EXISTS chain_depth INT NOT NULL DEFAULT 0;

COMMENT ON COLUMN runs.chain_depth IS
    'Profondeur dans l''arbre de chaînage : 0 pour un run lancé directement, parent + 1 sinon. '
    'Écrite à la création ; borne la récursion sans avoir à remonter la chaîne à chaque chaînage.';

-- La lecture de l'arbre complet, depuis n'importe lequel de ses membres.
CREATE INDEX IF NOT EXISTS idx_runs_root ON runs (root_run_id) WHERE root_run_id IS NOT NULL;

-- Le comptage des enfants directs d'un run, qui borne l'éventail d'un seul parent.
CREATE INDEX IF NOT EXISTS idx_runs_parent ON runs (parent_run_id) WHERE parent_run_id IS NOT NULL;
