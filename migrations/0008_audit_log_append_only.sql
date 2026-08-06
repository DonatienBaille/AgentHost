-- Agent Host — `audit_log` devient append-only (exigence WORM de la spécification §13)
--
-- Le journal d'audit répond à « qui a fait quoi, quand » : suppression d'un membre, accès à un
-- secret, approbation d'une porte. Sa valeur tient entièrement à ce qu'on ne puisse pas le
-- réécrire. Jusqu'ici rien ne l'en empêchait : un UPDATE ou un DELETE ordinaire passait, et
-- l'application se connecte avec des droits pleins sur la table.
--
-- Le garde-fou est un trigger qui lève. C'est délibérément un trigger et non un simple REVOKE :
-- un REVOKE sur le rôle applicatif ne protège de rien tant que ce rôle est propriétaire de la
-- table, puisqu'il peut se redonner le droit. Le trigger, lui, s'applique à *toute* session, y
-- compris celle du propriétaire, et y compris à un ordre lancé à la main dans psql.
--
-- CE QUE ÇA NE PROTÈGE PAS, et il faut le dire :
--
--   * un rôle capable de faire `ALTER TABLE audit_log DISABLE TRIGGER` ou `DROP TRIGGER` contourne
--     tout. Le propriétaire de la table en est capable, et l'application est aujourd'hui
--     propriétaire. Un WORM réel demande une séparation de rôles — l'application écrit avec un rôle
--     qui n'est pas propriétaire, la migration s'exécute avec un rôle d'administration distinct.
--     Cette migration ne fait pas cette séparation : elle demanderait de revoir le déploiement
--     entier (docker-compose, Helm, chaîne de connexion) et se décide au niveau de l'exploitation,
--     pas d'un fichier SQL. Le trigger transforme une modification silencieuse en acte délibéré et
--     tracé, ce qui est déjà l'essentiel du gain ;
--   * une restauration de sauvegarde, qui recrée la table à partir du dump.
--
-- TRUNCATE est couvert par un trigger de niveau instruction distinct : un trigger BEFORE DELETE
-- FOR EACH ROW ne voit jamais un TRUNCATE, qui est justement le raccourci qu'emprunterait
-- quelqu'un voulant effacer le journal d'un coup.

CREATE OR REPLACE FUNCTION audit_log_is_append_only()
RETURNS TRIGGER
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION
        'audit_log est append-only : % refusé (spécification §13, WORM)', TG_OP
        USING ERRCODE = 'restrict_violation',
              HINT = 'Le journal d''audit ne se corrige pas : ajoutez une entrée qui décrit la '
                     'correction. Pour une purge réglementaire, désactivez explicitement le '
                     'trigger, tracez l''opération hors base, et réactivez-le.';
END;
$$;

COMMENT ON FUNCTION audit_log_is_append_only() IS
    'Lève sur toute tentative de UPDATE, DELETE ou TRUNCATE sur audit_log (WORM, spec §13).';

DROP TRIGGER IF EXISTS audit_log_no_update ON audit_log;
CREATE TRIGGER audit_log_no_update
    BEFORE UPDATE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_is_append_only();

DROP TRIGGER IF EXISTS audit_log_no_delete ON audit_log;
CREATE TRIGGER audit_log_no_delete
    BEFORE DELETE ON audit_log
    FOR EACH ROW EXECUTE FUNCTION audit_log_is_append_only();

DROP TRIGGER IF EXISTS audit_log_no_truncate ON audit_log;
CREATE TRIGGER audit_log_no_truncate
    BEFORE TRUNCATE ON audit_log
    FOR EACH STATEMENT EXECUTE FUNCTION audit_log_is_append_only();

COMMENT ON TABLE audit_log IS
    'Append-only (triggers audit_log_no_update / _no_delete / _no_truncate). '
    'Une entrée erronée se corrige en ajoutant une entrée, jamais en modifiant l''ancienne.';
