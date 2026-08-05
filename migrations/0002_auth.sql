-- Agent Host — authentication support (JWT login)
-- Adds password storage to users so POST /api/auth/login can verify credentials.

ALTER TABLE users
    ADD COLUMN password_hash VARCHAR(255);
