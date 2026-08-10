-- 0014 — Un compteur de runs par projet, à la place du « MAX(number) + 1 ».
--
-- Le défaut corrigé ici a été trouvé par un test de concurrence, pas par relecture : douze
-- créations simultanées sur le même projet, cinq refusées avec une erreur 500. La numérotation
-- passait par un verrou consultatif (`pg_advisory_lock`) pris autour de la LECTURE du maximum
-- puis RELÂCHÉ avant l'insertion. Le verrou ne couvrait donc pas l'intervalle qui compte : deux
-- appelants pouvaient lire le même maximum l'un après l'autre — chacun à son tour, verrou en
-- main, mais aucun n'ayant encore inséré — obtenir le même numéro, et se heurter ensuite à
-- `UNIQUE (project_id, number)`.
--
-- Un compteur porté par la ligne du projet supprime l'intervalle plutôt que de le protéger :
-- `UPDATE ... RETURNING` incrémente et rend la valeur en une seule instruction, dont le verrou de
-- ligne sérialise les concurrents sans qu'aucun code applicatif n'ait à le demander. C'est la même
-- primitive que `AddBudgetUsageAsync`, qui, elle, était juste depuis le début.
--
-- Une SEQUENCE par projet ferait aussi l'affaire mais coûterait un objet de schéma par ligne de
-- `projects` — ingérable en multi-locataire.
--
-- Conséquence assumée : une création qui échoue APRÈS l'allocation laisse un trou dans la
-- numérotation. C'est le comportement de toute séquence, et un numéro de run est un identifiant
-- lisible, pas un décompte.

ALTER TABLE projects ADD COLUMN IF NOT EXISTS run_counter BIGINT NOT NULL DEFAULT 0;

-- Reprise de l'existant : le compteur doit repartir du plus grand numéro déjà attribué, sinon les
-- premiers runs après migration entreraient en collision avec les anciens.
UPDATE projects p
SET run_counter = COALESCE((SELECT MAX(r.number) FROM runs r WHERE r.project_id = p.id), 0)
WHERE run_counter = 0;
