-- Agent Host — initial schema (spec v2.0, section 5)
-- PostgreSQL 16+

-- Organizations (tenants)
CREATE TABLE organizations (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    name VARCHAR(255) NOT NULL,
    slug VARCHAR(100) NOT NULL UNIQUE,
    plan VARCHAR(50) NOT NULL, -- free, team, business, enterprise

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMP NULL
);

-- Projects (grouping within org)
CREATE TABLE projects (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),
    name VARCHAR(255) NOT NULL,
    slug VARCHAR(100) NOT NULL,
    description TEXT,

    budget_monthly_usd DECIMAL(10, 2) DEFAULT 1000,

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMP NULL,

    UNIQUE (org_id, slug)
);

-- Agents (versioned definitions)
CREATE TABLE agents (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),
    project_id VARCHAR(50) NOT NULL REFERENCES projects(id),
    name VARCHAR(255) NOT NULL,
    slug VARCHAR(100) NOT NULL,

    agent_type VARCHAR(50) NOT NULL, -- oci, copilot, claude_code, openai, custom
    image_ref VARCHAR(500), -- for OCI agents

    manifest_yaml TEXT NOT NULL, -- full YAML content

    inputs_schema JSONB NOT NULL, -- JSON Schema for inputs
    outputs_schema JSONB, -- JSON Schema for outputs

    current_version_id VARCHAR(50),

    is_published BOOLEAN NOT NULL DEFAULT FALSE,

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMP NULL,

    UNIQUE (org_id, slug)
);

-- Agent versions (immutable snapshots)
CREATE TABLE agent_versions (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    agent_id VARCHAR(50) NOT NULL REFERENCES agents(id),
    version_number INTEGER NOT NULL,

    manifest_yaml TEXT NOT NULL,
    image_ref VARCHAR(500),
    inputs_schema JSONB NOT NULL,
    outputs_schema JSONB,

    digest_sha256 VARCHAR(64) NOT NULL UNIQUE, -- content hash

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),

    UNIQUE (agent_id, version_number)
);

ALTER TABLE agents
    ADD CONSTRAINT fk_agents_current_version
    FOREIGN KEY (current_version_id) REFERENCES agent_versions(id);

-- Runs (executions)
CREATE TABLE runs (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),
    project_id VARCHAR(50) NOT NULL REFERENCES projects(id),
    number BIGINT NOT NULL, -- sequential per project

    agent_id VARCHAR(50) NOT NULL REFERENCES agents(id),
    agent_version_id VARCHAR(50) NOT NULL REFERENCES agent_versions(id),

    status VARCHAR(50) NOT NULL, -- pending, queued, provisioning, preparing, running, awaiting_approval, awaiting_input, finalizing, succeeded, failed, cancelled, timed_out, budget_exceeded, rejected, infra_error

    inputs JSONB NOT NULL, -- user inputs, validated
    context JSONB NOT NULL, -- repo, commit, issue, etc.
    outputs JSONB, -- agent outputs

    workspace_path VARCHAR(500), -- /var/agenthost/runs/{id}/

    duration_ms BIGINT, -- total execution time
    exit_code INT, -- process exit code

    error_message TEXT,
    error_code VARCHAR(100), -- policy_violation, timeout, budget_exceeded, etc.

    budget_max_usd DECIMAL(10, 2),
    budget_used_usd DECIMAL(10, 2),

    triggered_by_user_id VARCHAR(50),
    triggered_by_type VARCHAR(50), -- manual, webhook, cron, api, chain

    parent_run_id VARCHAR(50),
    root_run_id VARCHAR(50),

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    started_at TIMESTAMP,
    finished_at TIMESTAMP,
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMP NULL,

    UNIQUE (project_id, number)
);

-- Run events (append-only, immutable)
CREATE TABLE run_events (
    run_id VARCHAR(50) NOT NULL REFERENCES runs(id),
    seq BIGINT NOT NULL, -- monotone, no gaps
    timestamp TIMESTAMP NOT NULL DEFAULT NOW(),

    event_type VARCHAR(100) NOT NULL, -- phase.started, tool.called, log, approval.requested, etc.
    level VARCHAR(20) NOT NULL, -- debug, info, warn, error

    message TEXT,
    payload JSONB, -- event-specific data

    PRIMARY KEY (run_id, seq)
);

-- Approvals (human-in-the-loop)
CREATE TABLE approvals (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    run_id VARCHAR(50) NOT NULL REFERENCES runs(id),
    step_id VARCHAR(50),

    approval_type VARCHAR(50) NOT NULL, -- gate, question, budget_increase
    prompt TEXT NOT NULL,
    options JSONB, -- possible answers

    required_role VARCHAR(50), -- developer, maintainer, owner
    required_count INT NOT NULL DEFAULT 1,

    responses JSONB, -- [{by, decision, at, note}, ...]

    status VARCHAR(50) NOT NULL, -- pending, approved, rejected, expired
    expires_at TIMESTAMP NOT NULL,
    decided_at TIMESTAMP,
    decided_by VARCHAR(50),

    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Project Memory (dream-like context)
CREATE TABLE project_memories (
    project_id VARCHAR(50) PRIMARY KEY REFERENCES projects(id),

    context JSONB NOT NULL DEFAULT '{}', -- {name, description, technologies, recentDecisions}

    run_history JSONB NOT NULL DEFAULT '[]', -- [{runId, agent, timestamp, outcome, summary}]
    patterns JSONB NOT NULL DEFAULT '[]', -- [{pattern, frequency, lastSeen}]
    learnings JSONB NOT NULL DEFAULT '[]', -- [{id, title, description, createdAt, appliedBy}]
    notes JSONB NOT NULL DEFAULT '[]', -- [{id, agent, text, timestamp}]

    archived JSONB NOT NULL DEFAULT '[]', -- [{period, summary, createdAt}]

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Artifacts (outputs from runs)
CREATE TABLE artifacts (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    run_id VARCHAR(50) NOT NULL REFERENCES runs(id),

    name VARCHAR(255) NOT NULL, -- e.g., "fix.patch", "report.md"
    artifact_type VARCHAR(50), -- patch, report, data, logs

    s3_path VARCHAR(500) NOT NULL, -- s3://bucket/artifacts/{runId}/{name}
    size_bytes BIGINT,

    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Users
CREATE TABLE users (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),

    email VARCHAR(255) NOT NULL UNIQUE,
    display_name VARCHAR(255),

    avatar_url VARCHAR(500),

    role VARCHAR(50) NOT NULL, -- owner, maintainer, developer, viewer

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMP NULL
);

-- Audit log (WORM - write-once, read-many)
CREATE TABLE audit_log (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),

    action VARCHAR(255) NOT NULL, -- run.created, run.approved, secret.accessed, etc.
    actor_user_id VARCHAR(50),

    resource_type VARCHAR(100), -- run, agent, secret, project
    resource_id VARCHAR(50),

    changes JSONB, -- what changed
    details JSONB, -- extra context

    created_at TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Secrets (encrypted)
CREATE TABLE secrets (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    org_id VARCHAR(50) NOT NULL REFERENCES organizations(id),
    project_id VARCHAR(50) REFERENCES projects(id),

    name VARCHAR(255) NOT NULL,

    encrypted_value BYTEA, -- AES-GCM ciphertext (Postgres-encrypted default backend)
    vault_path VARCHAR(500), -- secret/org/{orgId}/secrets/{id} (when Vault backend enabled)
    digest_sha256_truncated VARCHAR(16), -- for leak detection

    scope VARCHAR(50) NOT NULL, -- org, project

    last_used_at TIMESTAMP,
    last_used_by_run_id VARCHAR(50),

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW(),
    deleted_at TIMESTAMP NULL,

    UNIQUE (org_id, name)
);

-- Webhooks
CREATE TABLE webhooks (
    id VARCHAR(50) PRIMARY KEY, -- ULID
    project_id VARCHAR(50) NOT NULL REFERENCES projects(id),

    url VARCHAR(500) NOT NULL,
    events JSONB NOT NULL, -- ["run.created", "run.finished"]

    secret_token VARCHAR(255), -- for HMAC signing

    is_active BOOLEAN NOT NULL DEFAULT TRUE,

    created_at TIMESTAMP NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMP NOT NULL DEFAULT NOW()
);

-- Indexes for common queries
CREATE INDEX idx_runs_org ON runs(org_id);
CREATE INDEX idx_runs_project ON runs(project_id);
CREATE INDEX idx_runs_agent ON runs(agent_id);
CREATE INDEX idx_runs_status ON runs(status);
CREATE INDEX idx_runs_org_status ON runs(org_id, status);
CREATE INDEX idx_runs_project_created ON runs(project_id, created_at DESC);

CREATE INDEX idx_run_events_run_timestamp ON run_events(run_id, timestamp DESC);
CREATE INDEX idx_run_events_type ON run_events(run_id, event_type);

CREATE INDEX idx_approvals_run_status ON approvals(run_id, status);

CREATE INDEX idx_artifacts_run ON artifacts(run_id);

CREATE INDEX idx_agents_org ON agents(org_id);
CREATE INDEX idx_agents_project ON agents(project_id);

CREATE INDEX idx_audit_org_action ON audit_log(org_id, action);
CREATE INDEX idx_audit_resource ON audit_log(org_id, resource_type, resource_id);

CREATE INDEX idx_secrets_org ON secrets(org_id);
