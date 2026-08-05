-- Agent Host — enum columns store snake_case strings, not enum ordinals
--
-- Every enum-typed column was being written by Dapper as the CLR enum's *ordinal*, rendered as
-- text ('14', '0', ...), because Dapper short-circuits enum parameters to their underlying
-- integral type before consulting a registered SqlMapper.TypeHandler. The ordinal round-tripped
-- consistently (reads went through Enum.Parse, which happily parses "14" back to the member at
-- position 14), so nothing visibly broke — but every SQL predicate written against the documented
-- values silently matched zero rows. Most damagingly SecretRepository.ListForScopeAsync's
-- `scope = 'org' OR (scope = 'project' AND ...)` never matched, so no run ever received a secret.
--
-- This migration rewrites the persisted data into the documented lowercase/snake_case encoding
-- (the values produced by each enum's ToDbString() in Domain/Enums.cs) and then pins that encoding
-- with CHECK constraints, so a future write that reintroduces the ordinal encoding fails loudly at
-- the database instead of silently corrupting query results.
--
-- Ordinals are mapped using the *declaration order* in Domain/Enums.cs, which is what the stored
-- numbers mean. Each UPDATE is guarded by `~ '^[0-9]+$'` so it only touches rows that still hold
-- the integer form; rows already in the string form (and re-runs of this migration) are untouched.

-- runs.status — RunStatus
UPDATE runs SET status = CASE status
    WHEN '0'  THEN 'pending'
    WHEN '1'  THEN 'queued'
    WHEN '2'  THEN 'provisioning'
    WHEN '3'  THEN 'preparing'
    WHEN '4'  THEN 'running'
    WHEN '5'  THEN 'awaiting_approval'
    WHEN '6'  THEN 'awaiting_input'
    WHEN '7'  THEN 'finalizing'
    WHEN '8'  THEN 'succeeded'
    WHEN '9'  THEN 'failed'
    WHEN '10' THEN 'cancelled'
    WHEN '11' THEN 'timed_out'
    WHEN '12' THEN 'budget_exceeded'
    WHEN '13' THEN 'rejected'
    WHEN '14' THEN 'infra_error'
    ELSE status
END
WHERE status ~ '^[0-9]+$';

-- runs.triggered_by_type — TriggeredByType (nullable column)
UPDATE runs SET triggered_by_type = CASE triggered_by_type
    WHEN '0' THEN 'manual'
    WHEN '1' THEN 'webhook'
    WHEN '2' THEN 'cron'
    WHEN '3' THEN 'api'
    WHEN '4' THEN 'chain'
    ELSE triggered_by_type
END
WHERE triggered_by_type ~ '^[0-9]+$';

-- approvals.approval_type — ApprovalType
UPDATE approvals SET approval_type = CASE approval_type
    WHEN '0' THEN 'gate'
    WHEN '1' THEN 'question'
    WHEN '2' THEN 'budget_increase'
    ELSE approval_type
END
WHERE approval_type ~ '^[0-9]+$';

-- approvals.status — ApprovalStatus
UPDATE approvals SET status = CASE status
    WHEN '0' THEN 'pending'
    WHEN '1' THEN 'approved'
    WHEN '2' THEN 'rejected'
    WHEN '3' THEN 'expired'
    ELSE status
END
WHERE status ~ '^[0-9]+$';

-- agents.agent_type — AgentType
UPDATE agents SET agent_type = CASE agent_type
    WHEN '0' THEN 'oci'
    WHEN '1' THEN 'copilot'
    WHEN '2' THEN 'claude_code'
    WHEN '3' THEN 'openai'
    WHEN '4' THEN 'custom'
    ELSE agent_type
END
WHERE agent_type ~ '^[0-9]+$';

-- users.role — UserRole
UPDATE users SET role = CASE role
    WHEN '0' THEN 'owner'
    WHEN '1' THEN 'maintainer'
    WHEN '2' THEN 'developer'
    WHEN '3' THEN 'viewer'
    ELSE role
END
WHERE role ~ '^[0-9]+$';

-- secrets.scope — SecretScope
UPDATE secrets SET scope = CASE scope
    WHEN '0' THEN 'org'
    WHEN '1' THEN 'project'
    ELSE scope
END
WHERE scope ~ '^[0-9]+$';

-- Pin the encoding. These constraints are what makes the ordinal encoding structurally
-- impossible to reintroduce: an INSERT/UPDATE binding a raw CLR enum writes '3', which no longer
-- satisfies any of these, and the statement fails instead of quietly storing a value that every
-- documented predicate will miss.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_runs_status') THEN
        ALTER TABLE runs ADD CONSTRAINT chk_runs_status CHECK (status IN (
            'pending', 'queued', 'provisioning', 'preparing', 'running', 'awaiting_approval',
            'awaiting_input', 'finalizing', 'succeeded', 'failed', 'cancelled', 'timed_out',
            'budget_exceeded', 'rejected', 'infra_error'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_runs_triggered_by_type') THEN
        ALTER TABLE runs ADD CONSTRAINT chk_runs_triggered_by_type CHECK (
            triggered_by_type IS NULL
            OR triggered_by_type IN ('manual', 'webhook', 'cron', 'api', 'chain'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_approvals_approval_type') THEN
        ALTER TABLE approvals ADD CONSTRAINT chk_approvals_approval_type CHECK (
            approval_type IN ('gate', 'question', 'budget_increase'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_approvals_status') THEN
        ALTER TABLE approvals ADD CONSTRAINT chk_approvals_status CHECK (
            status IN ('pending', 'approved', 'rejected', 'expired'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_agents_agent_type') THEN
        ALTER TABLE agents ADD CONSTRAINT chk_agents_agent_type CHECK (
            agent_type IN ('oci', 'copilot', 'claude_code', 'openai', 'custom'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_users_role') THEN
        ALTER TABLE users ADD CONSTRAINT chk_users_role CHECK (
            role IN ('owner', 'maintainer', 'developer', 'viewer'));
    END IF;

    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_secrets_scope') THEN
        ALTER TABLE secrets ADD CONSTRAINT chk_secrets_scope CHECK (scope IN ('org', 'project'));
    END IF;
END
$$;
