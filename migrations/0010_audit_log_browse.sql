-- Agent Host — le journal d'audit devient consultable (feuille de route, lot 3)
--
-- LE PROBLÈME. Le journal était servi par une seule requête : `WHERE org_id = ? ORDER BY
-- created_at DESC LIMIT ? OFFSET ?`. C'est une table brute paginée : au-delà de quelques centaines
-- d'entrées, retrouver « qui a changé le rôle de cet utilisateur, et quand » revient à feuilleter.
-- Un journal d'audit qu'on ne sait pas interroger ne remplit pas la fonction pour laquelle il
-- existe — on ne le consulte pas par curiosité, on le consulte quand quelque chose s'est produit.
--
-- CE QUE CETTE MIGRATION CHANGE. Rien au schéma : aucune colonne, aucune contrainte. Uniquement
-- les index qui rendent les filtres tenables. La table reste append-only (voir 0008).
--
-- POURQUOI `created_at DESC` DANS LES INDEX. Toutes les lectures de ce journal sont ordonnées du
-- plus récent au plus ancien — c'est le seul ordre qui ait du sens pour un journal. Un index sur
-- `(org_id, action)` seul sait trouver les lignes, mais Postgres doit ensuite les trier ; en
-- incluant `created_at DESC` dans la clé, le tri disparaît et le LIMIT s'arrête à la première page
-- lue. Sur une table qui ne fait que grossir, c'est la différence entre un coût constant et un coût
-- proportionnel à l'historique de l'organisation.
--
-- POURQUOI `idx_audit_org_action` EST REMPLACÉ. Le nouvel index `(org_id, action, created_at DESC)`
-- a `(org_id, action)` pour préfixe : il répond à tout ce que l'ancien répondait, et au tri en
-- plus. Garder les deux, c'est payer une écriture supplémentaire à chaque entrée du journal pour un
-- index que le planificateur ne choisira plus.

-- Le parcours par défaut : la dernière page du journal d'une organisation, sans aucun filtre.
CREATE INDEX IF NOT EXISTS idx_audit_org_created ON audit_log (org_id, created_at DESC);

-- Filtre par action, et filtre par période à l'intérieur d'une action.
CREATE INDEX IF NOT EXISTS idx_audit_org_action_created ON audit_log (org_id, action, created_at DESC);
DROP INDEX IF EXISTS idx_audit_org_action;

-- Filtre par acteur : « qu'a fait cette personne ». Partiel, parce que les entrées système
-- (réinitialisation demandée par un inconnu, révocation automatique) portent un acteur NULL et
-- qu'aucun filtre ne les cherche par acteur.
CREATE INDEX IF NOT EXISTS idx_audit_org_actor_created
    ON audit_log (org_id, actor_user_id, created_at DESC)
    WHERE actor_user_id IS NOT NULL;
