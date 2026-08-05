-- Agent Host — refresh tokens (token revocation + short-lived access tokens)
--
-- Access tokens are stateless JWTs and therefore cannot be revoked server-side; the mitigation
-- is a short TTL (Jwt:ExpiryMinutes, 15 minutes) paired with a long-lived, *persisted* refresh
-- token that CAN be revoked. Only a SHA-256 hash of the refresh token is stored, so a database
-- disclosure does not hand out usable credentials.

CREATE TABLE refresh_tokens (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    user_id VARCHAR(50) NOT NULL REFERENCES users(id),
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),

    token_hash VARCHAR(64) NOT NULL UNIQUE, -- SHA-256 hex of the raw token; the raw value is never stored

    expires_at TIMESTAMP NOT NULL,
    revoked_at TIMESTAMP NULL,
    replaced_by_id VARCHAR(50) NULL, -- set on rotation, for reuse-detection forensics

    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE INDEX idx_refresh_tokens_user ON refresh_tokens(user_id);
CREATE INDEX idx_refresh_tokens_hash ON refresh_tokens(token_hash);
