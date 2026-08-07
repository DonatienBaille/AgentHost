-- Agent Host — déclencheurs entrants : webhooks et planification (feuille de route, lot 4)
--
-- CE QUI MANQUAIT. `TriggeredByType` déclare depuis l'origine `manual`, `webhook`, `cron`, `api`
-- et `chain`. Seuls `manual` et `api` fonctionnaient : les trois autres étaient des valeurs
-- d'énumération sans aucun code derrière. Un agent ne pouvait donc être lancé que par quelqu'un
-- qui clique ou qui appelle l'API avec un jeton — c'est-à-dire jamais tout seul, ce qui est
-- pourtant la raison d'être d'une plateforme d'agents autonomes.
--
-- UNE TABLE, DEUX NATURES. `webhook` et `cron` répondent à la même question — « à quelle occasion
-- ce run part-il tout seul » — et partagent tout le reste : la cible (projet, agent), les entrées
-- fixes, l'activation, la traçabilité. Les séparer en deux tables dupliquerait ces colonnes et
-- imposerait deux endpoints de gestion, deux pages, deux jeux de tests, pour une différence qui
-- tient à trois colonnes. Les colonnes propres à chaque nature sont donc nullables, et une
-- contrainte CHECK garantit que chaque ligne porte bien celles de son type — un déclencheur cron
-- sans expression, ou webhook sans secret, est rejeté par la base et non par une validation
-- applicative qu'on peut contourner.
--
-- POURQUOI PAS LA TABLE `webhooks` EXISTANTE. Elle décrit l'inverse : les webhooks SORTANTS, ce
-- que la plateforme notifie à des tiers. Le sens du secret y est même opposé — là-bas nous
-- signons, ici nous vérifions. Réutiliser la table aurait mélangé deux flux de confiance
-- contraires dans les mêmes lignes.

CREATE TABLE IF NOT EXISTS triggers (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),
    project_id VARCHAR(50) NOT NULL REFERENCES projects(id),
    agent_id VARCHAR(50) NOT NULL REFERENCES agents(id),

    type VARCHAR(20) NOT NULL,
    name VARCHAR(255) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,

    -- Entrées fixes injectées dans chaque run déclenché. Un déclencheur n'a pas d'utilisateur
    -- devant lui pour remplir un formulaire : ce qu'il ne porte pas ici, le run ne l'aura pas.
    inputs JSONB NOT NULL DEFAULT '{}'::jsonb,

    -- ---- propre aux déclencheurs webhook ----

    -- Secret HMAC, chiffré au même titre que les secrets applicatifs (AES-256-GCM, clé hôte).
    -- En clair, il permettrait à quiconque lit la base de forger un déclenchement.
    secret_encrypted BYTEA,

    -- github, gitlab, generic : détermine l'en-tête de signature attendu et son calcul.
    provider VARCHAR(20),

    -- {"events":["push"],"branches":["main","release/*"]} — filtre appliqué à la livraison.
    event_filter JSONB,

    -- ---- propre aux déclencheurs cron ----

    cron_expression VARCHAR(100),

    -- Fuseau d'interprétation de l'expression. « tous les jours à 9 h » ne veut rien dire sans
    -- lui, et UTC comme seul choix condamnerait une équipe non britannique à faire le calcul de
    -- tête deux fois par an.
    timezone VARCHAR(64) NOT NULL DEFAULT 'UTC',

    -- Prochaine échéance, calculée à l'écriture. C'est elle que le planificateur interroge :
    -- comparer une date indexée coûte infiniment moins que réévaluer toutes les expressions cron
    -- de l'installation à chaque battement.
    next_run_at TIMESTAMP,
    last_run_at TIMESTAMP,
    last_run_id VARCHAR(50),

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMP NULL,

    CONSTRAINT triggers_type_check CHECK (type IN ('webhook', 'cron')),
    CONSTRAINT triggers_provider_check
        CHECK (provider IS NULL OR provider IN ('github', 'gitlab', 'generic')),

    -- Chaque nature porte ce qu'il lui faut, et la base le garantit. Un webhook sans secret ne
    -- serait pas « moins sécurisé » : il serait un endpoint public capable de lancer des runs.
    CONSTRAINT triggers_shape_check CHECK (
        (type = 'webhook' AND secret_encrypted IS NOT NULL AND provider IS NOT NULL)
        OR
        (type = 'cron' AND cron_expression IS NOT NULL)
    )
);

-- Le parcours du planificateur : les échéances dues, tous projets confondus. Partiel, parce que
-- les déclencheurs webhook n'ont pas d'échéance et que les inactifs ne doivent jamais partir.
CREATE INDEX IF NOT EXISTS idx_triggers_due
    ON triggers (next_run_at)
    WHERE type = 'cron' AND is_active AND deleted_at IS NULL;

-- Le parcours de l'IHM : les déclencheurs d'un projet.
CREATE INDEX IF NOT EXISTS idx_triggers_project
    ON triggers (project_id, created_at DESC)
    WHERE deleted_at IS NULL;

-- Les livraisons déjà traitées, pour ne pas relancer un run sur une nouvelle tentative.
--
-- POURQUOI CETTE TABLE EXISTE. GitHub réémet une livraison quand la réponse tarde ou échoue, et
-- offre un bouton « redeliver ». Sans mémoire, chaque réémission relancerait l'agent : le premier
-- run coûterait de l'argent, le second aussi, et rien dans l'IHM ne dirait qu'il s'agit du même
-- événement. L'unicité est portée par la base et non par une vérification applicative, parce que
-- deux réémissions simultanées passeraient toutes les deux le test avant que l'une n'écrive.
CREATE TABLE IF NOT EXISTS trigger_deliveries (
    trigger_id VARCHAR(50) NOT NULL REFERENCES triggers(id),

    -- Identifiant fourni par l'émetteur (X-GitHub-Delivery, X-Gitlab-Event-UUID, …). À défaut,
    -- l'empreinte du corps : deux livraisons identiques d'un émetteur sans identifiant sont
    -- indiscernables, et les traiter comme un doublon vaut mieux que de facturer deux runs.
    delivery_id VARCHAR(128) NOT NULL,

    run_id VARCHAR(50),
    received_at TIMESTAMP NOT NULL DEFAULT NOW(),

    PRIMARY KEY (trigger_id, delivery_id)
);

-- Purge : ces lignes ne servent qu'à la déduplication, dont la fenêtre utile est celle des
-- réémissions (quelques heures chez GitHub). L'index rend le ménage périodique bon marché.
CREATE INDEX IF NOT EXISTS idx_trigger_deliveries_received
    ON trigger_deliveries (received_at);

COMMENT ON TABLE triggers IS
    'Déclencheurs entrants d''un agent : webhook (signature vérifiée) ou cron (planifié). '
    'Les colonnes propres à chaque type sont nullables et garanties par triggers_shape_check.';
