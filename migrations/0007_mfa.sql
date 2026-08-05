-- Agent Host — TOTP multi-factor authentication (RFC 6238) and its recovery codes
--
-- Two tables rather than columns on `users`, so that the shared-secret material lives somewhere
-- with its own access story and `SELECT * FROM users` never drags a TOTP seed along with it.
--
-- user_mfa.secret_encrypted holds the AES-256-GCM ciphertext produced by ISecretsBroker.Encrypt
-- (layout nonce(12) || ciphertext || tag(16), key from Secrets:EncryptionKey) — the same
-- encryption the secrets table uses. A TOTP seed is a *symmetric* credential: anyone who reads it
-- can generate valid codes forever, which is why it is never stored in the clear, unlike a password
-- (which is hashed and therefore not recoverable at all).
--
-- `enabled` is separate from the row's existence because enrollment is two-phase: POST
-- /api/auth/mfa/enroll writes the secret with enabled = FALSE, and only a correct code at
-- /api/auth/mfa/confirm flips it to TRUE. Without that, a user who scanned the QR code wrongly
-- would lock themselves out of their own account.
--
-- Recovery codes are the escape hatch when the authenticator device is lost. They are stored as
-- SHA-256 hashes exactly like every other opaque token in this schema, shown to the user once at
-- confirmation time, and single-use: `used_at` is set by a conditional UPDATE, so replaying a code
-- that has already been spent fails.

CREATE TABLE user_mfa (
    user_id VARCHAR(50) PRIMARY KEY REFERENCES users(id),
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),

    secret_encrypted BYTEA NOT NULL, -- AES-256-GCM, see ISecretsBroker; never stored in plaintext

    enabled BOOLEAN NOT NULL DEFAULT FALSE, -- FALSE until a correct code confirms the enrollment
    confirmed_at TIMESTAMP NULL,

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW()
);

CREATE TABLE mfa_recovery_codes (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    user_id VARCHAR(50) NOT NULL REFERENCES users(id),

    code_hash VARCHAR(64) NOT NULL, -- SHA-256 hex of the normalized code; the raw code is never stored

    used_at TIMESTAMP NULL, -- set on first use; a spent code can never be replayed
    created_at TIMESTAMP NOT NULL DEFAULT NOW(),

    CONSTRAINT uq_mfa_recovery_codes_user_hash UNIQUE (user_id, code_hash)
);

CREATE INDEX idx_mfa_recovery_codes_user ON mfa_recovery_codes(user_id);
