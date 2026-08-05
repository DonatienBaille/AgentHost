-- Agent Host — password reset tokens
--
-- Same hash-only shape as refresh_tokens and invitations: only the SHA-256 hex of the raw token is
-- stored, so this table hands out no usable credential if it is read. The expiry is deliberately
-- much shorter than an invitation's (see PasswordResetTokenLifetime in AuthService) because a reset
-- token is a full account-takeover primitive for as long as it lives.
--
-- Single-use: `used_at` is set the moment a reset is confirmed, by a conditional UPDATE, so two
-- concurrent confirmations cannot both succeed. `revoked_at` lets every other outstanding token for
-- the same user be invalidated when one of them is spent — otherwise a stale token from an earlier
-- request would still work against the newly chosen password.
--
-- There is no mailer in this system. See POST /api/auth/password-reset/request: in production the
-- minted token is not returned to the anonymous caller and not delivered anywhere, which means the
-- flow is incomplete until a mailer exists. Returning it is possible only behind the
-- Auth:ReturnResetTokenInResponse dev/test flag.

CREATE TABLE password_reset_tokens (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    user_id VARCHAR(50) NOT NULL REFERENCES users(id),
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),

    token_hash VARCHAR(64) NOT NULL UNIQUE, -- SHA-256 hex; the raw token is never stored

    expires_at TIMESTAMP NOT NULL,
    used_at TIMESTAMP NULL,
    revoked_at TIMESTAMP NULL,

    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_password_reset_tokens_user ON password_reset_tokens(user_id);
CREATE INDEX idx_password_reset_tokens_hash ON password_reset_tokens(token_hash);
