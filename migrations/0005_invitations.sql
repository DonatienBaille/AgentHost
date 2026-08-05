-- Agent Host — organization invitations
--
-- Before this table there were exactly two ways into an organization: self-registration (which
-- always creates a *new* org, so it cannot add a member to an existing one) and POST /api/users,
-- where the admin chooses the new user's password — meaning every user's initial password was
-- necessarily known to someone else. An invitation closes that gap: the admin names an email and a
-- role, and the invitee chooses their own password when they accept.
--
-- Only a SHA-256 hash of the invitation token is stored, exactly like refresh_tokens: the raw
-- token is returned to the inviter once, at creation, and never again by any endpoint. There is no
-- mailer in this system yet, so handing the raw token to the invitee is an out-of-band, human step.
--
-- `role` follows the project-wide enum encoding: a lowercase string pinned by a CHECK constraint
-- (see 0004_enum_string_encoding.sql for why the ordinal encoding is structurally forbidden).

CREATE TABLE invitations (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),

    email VARCHAR(255) NOT NULL,
    role VARCHAR(20) NOT NULL,

    token_hash VARCHAR(64) NOT NULL UNIQUE, -- SHA-256 hex; the raw token is never stored

    invited_by_user_id VARCHAR(50) NOT NULL REFERENCES users(id),

    expires_at TIMESTAMP NOT NULL,
    accepted_at TIMESTAMP NULL, -- set once; an accepted invitation is spent
    revoked_at TIMESTAMP NULL,

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),

    CONSTRAINT chk_invitations_role CHECK (role IN ('owner', 'maintainer', 'developer', 'viewer'))
);

CREATE INDEX idx_invitations_org ON invitations(org_id);
CREATE INDEX idx_invitations_hash ON invitations(token_hash);
CREATE INDEX idx_invitations_email ON invitations(LOWER(email));
