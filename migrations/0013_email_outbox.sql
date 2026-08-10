-- Agent Host — la file d'envoi de courriels devient durable (dette identifiée, hors lots)
--
-- CE QUI ÉTAIT EN JEU. La file était un canal en mémoire : un arrêt brutal du processus perdait les
-- messages non encore acheminés, et un échec de remise n'était jamais réessayé. Pour une invitation
-- ou une réinitialisation, cela veut dire un utilisateur qui attend un courriel qui ne viendra
-- jamais, sans que rien nulle part ne le signale.
--
-- LA TENSION QUE CETTE TABLE OBLIGE À TRANCHER, ET COMMENT ELLE EST TRANCHÉE.
--
-- Le corps d'un de ces messages contient le JETON EN CLAIR — c'est tout son objet. Or le dépôt ne
-- stocke jamais un jeton autrement que sous forme d'empreinte SHA-256, précisément pour qu'une
-- lecture de la base ne donne pas de quoi prendre la main sur un compte. Persister le corps tel quel
-- reviendrait à défaire cette propriété par la porte de service : une ligne en attente serait un
-- lien de réinitialisation utilisable, lisible par quiconque lit la table.
--
-- Le corps est donc **chiffré** (AES-256-GCM, `ISecretsBroker`, la même clé et la même rotation que
-- les secrets applicatifs). Une ligne en attente n'expose alors pas plus que la table `secrets`, et
-- `--rekey-secrets` la couvre sans traitement particulier. Le sujet, lui, reste en clair : il ne
-- porte rien de confidentiel et le garder lisible rend la table diagnosticable.
--
-- ET LES LIGNES PARTENT. Une remise réussie **supprime** la ligne — pas de `sent_at` à conserver,
-- pas d'historique : un message acheminé n'a plus aucune raison d'exister ici, et le conserver ne
-- ferait que prolonger l'exposition d'un secret pour rien. Ce que la remise a produit est déjà
-- tracé côté journal applicatif, sans le corps.

CREATE TABLE IF NOT EXISTS email_outbox (
    id VARCHAR(50) PRIMARY KEY, -- ULID

    to_address VARCHAR(320) NOT NULL, -- 320 = longueur maximale d'une adresse RFC 5321
    subject TEXT NOT NULL,

    -- Le corps, chiffré. Voir l'en-tête : il contient le jeton en clair.
    body_encrypted BYTEA NOT NULL,

    -- `password_reset`, `invitation`… Sert au diagnostic et aux traces, jamais à la logique.
    kind VARCHAR(64) NOT NULL,

    attempts INT NOT NULL DEFAULT 0,

    -- Quand retenter. C'est la colonne que le répartiteur interroge ; le recul exponentiel se
    -- traduit par une date de plus en plus lointaine plutôt que par une attente en mémoire, qui ne
    -- survivrait pas à un redémarrage.
    next_attempt_at TIMESTAMP NOT NULL DEFAULT NOW(),

    -- Dernière erreur, pour qu'un exploitant sache POURQUOI un message ne part pas. Jamais le corps.
    last_error TEXT,

    -- Renseignée quand on renonce définitivement. La ligne reste alors, sans être réessayée : un
    -- message abandonné en silence est un message dont personne n'apprend jamais l'existence.
    abandoned_at TIMESTAMP,

    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Le parcours du répartiteur : les messages dus et pas encore abandonnés. Partiel, parce que les
-- lignes abandonnées ne sont plus jamais candidates et n'ont pas à alourdir l'index.
CREATE INDEX IF NOT EXISTS idx_email_outbox_due
    ON email_outbox (next_attempt_at)
    WHERE abandoned_at IS NULL;

COMMENT ON TABLE email_outbox IS
    'File d''envoi durable des courriels transactionnels. Le corps est chiffré (il contient le '
    'jeton en clair) et la ligne est SUPPRIMÉE à la première remise réussie. Les lignes qui '
    'subsistent sont donc, par construction, des messages en attente ou abandonnés.';
