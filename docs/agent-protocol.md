# AgentHost agent protocol v1.0

How an agent running inside an AgentHost container talks back to the host.

Everything an agent needs is handed to it in environment variables at launch; everything it wants
to report goes back over HTTP to `/api/agent/...`, authenticated with a token that is scoped to
that one run.

`AGENTHOST_PROTOCOL_VERSION=1.0` identifies this contract.

---

## 1. Environment the agent receives

| Variable | Description |
| --- | --- |
| `AGENTHOST_RUN_ID` | ULID of this run. Use it in every callback URL. |
| `AGENTHOST_PROJECT_ID` | ULID of the project the run belongs to. |
| `AGENTHOST_INPUTS` | JSON document with the run's validated inputs. |
| `AGENTHOST_PROTOCOL_VERSION` | `1.0`. |
| `AGENTHOST_RUN_TOKEN` | Bearer token for the callback API (see below). |
| `AGENT_<KEY>` | One per `spec.external.config` entry, for non-OCI agent types. |
| `HTTP_PROXY` / `HTTPS_PROXY` / `NO_PROXY` | Only when `spec.permissions.network: allowlist`. All outbound HTTP(S) must go through this proxy — it is what enforces the allowlist. |
| `AGENTHOST_NETWORK_ALLOWLIST` | Only for `network: allowlist`: comma-separated hosts the run may reach. Informational. |

### Secrets are files, not environment variables

**Secrets are never exported as `SECRET_<NAME>` environment variables** (they were in earlier
builds; that is gone). Environment variables are readable by anyone who can run `docker inspect`
on the container and by every process inside it via `/proc/1/environ`, so shipping secrets that way
defeated the read-only secrets mount.

Read each secret from a file instead:

```
/run/secrets/<NAME>
```

One file per secret granted by the manifest's `spec.permissions.secrets`, mounted read-only.
Names are exactly the secret names from the manifest — no `SECRET_` prefix, no case change. The
file contains the raw secret value with no trailing newline added.

```bash
TOKEN="$(cat /run/secrets/GITHUB_TOKEN)"
```

The host deletes the whole `/run/secrets` backing directory as soon as the container exits, so a
secret is only readable for the lifetime of the run.

### Filesystem

The workspace is bind-mounted read-write at `/workspace`; its host path is recorded in
`runs.workspace_path`. It is the only durable place an agent may write.

The container root filesystem is **read-only** by default, with a small `tmpfs` mounted at `/tmp`.
An agent that genuinely cannot work under a read-only rootfs must opt in explicitly in its
manifest:

```yaml
spec:
  permissions:
    writableRootfs: true
```

## 2. Authentication

`AGENTHOST_RUN_TOKEN` is a short-lived JWT minted per launch. Send it as a normal bearer token:

```
Authorization: Bearer $AGENTHOST_RUN_TOKEN
```

Properties that matter:

* **Audience `agenthost-agent`**, distinct from the user-facing API's audience. A run token is
  rejected by every `/api/...` endpoint a human uses, and a user's JWT is rejected by every
  `/api/agent/...` endpoint. The two credential types are never interchangeable.
* **Bound to one run.** The token carries a `run_id` claim, and every endpoint verifies it equals
  the `{runId}` in the request path. A token for run A used against run B gets `403`, always —
  this is the protocol's core isolation guarantee.
* **Expiry** = the run's `spec.runtime.maxDurationSeconds` plus a margin (default 300s,
  configurable via `Jwt:RunTokenMarginSeconds`), so a winding-down agent can still post its final
  events after the run's own deadline.
* Not persisted anywhere and never returned by the REST API.

Error responses: `401` (missing/invalid/expired token), `403` (valid token, wrong run),
`404` (run or approval does not exist), `409` (the run is not in a state that allows the call),
`400` (malformed body).

Requests are rate limited per run (120/minute).

## 3. Endpoints

All paths are relative to the host's base URL. All bodies are JSON.

### `POST /api/agent/runs/{runId}/events`

Streams an event. It is persisted to `run_events` with a monotonic per-run sequence number **and**
broadcast live over SignalR to the `run-{runId}` group, which is what the run viewer subscribes to.

```jsonc
// request
{
  "eventType": "tool.called",     // required; dotted name, e.g. phase.started, log, tool.called
  "level": "info",                // debug | info | warn | error (default info)
  "message": "ran the linter",
  "payload": { "tool": "eslint" } // optional, any JSON
}

// 200
{ "seq": 42 }
```

### `POST /api/agent/runs/{runId}/outputs`

Publishes the agent's result document to `runs.outputs`. Idempotent — the last write wins. Written
with a targeted UPDATE so it cannot clobber concurrent usage accounting.

```jsonc
// request
{ "outputs": { "pullRequestUrl": "https://…", "filesChanged": 7 } }

// 200
{ "runId": "01J…", "outputs": { … } }
```

### `POST /api/agent/runs/{runId}/approvals`

Requests a human decision. Creates a pending `approvals` row and moves the run
`running → awaiting_approval`, so the run is genuinely parked until a human acts. Also emits the
`approval.requested` run event and dispatches the `approval.requested` webhook.

```jsonc
// request
{
  "prompt": "May I push to the default branch?",  // required
  "stepId": "step-7",                             // optional, echoed back on the decision
  "approvalType": "gate",                         // gate | question | budget_increase (default gate)
  "options": ["yes", "no"],                       // optional
  "requiredRole": "maintainer",                   // default: manifest spec.approvals.beforeWrite.requiredRole
  "requiredCount": 1,                             // default: manifest spec.approvals.beforeWrite.requiredCount
  "expiresInSeconds": 3600                        // default 3600, max 86400
}

// 201
{
  "approvalId": "01J…",
  "status": "pending",
  "runStatus": "awaiting_approval",
  "expiresAt": "2026-08-05T13:31:16Z"
}
```

`requiredRole` is enforced when a human decides: a caller whose role is below it cannot satisfy the
gate (owner > maintainer > developer > viewer).

Returns `409` if the run is not `running` — an approval can only be raised from a running run.

### `POST /api/agent/runs/{runId}/questions`

Same mechanics, but an `ApprovalType.Question` and the run moves `running → awaiting_input`. A
question is not a policy gate, so it does not inherit the manifest's `approvals.beforeWrite`
role/count.

```jsonc
// request
{ "prompt": "Which database should I target?", "options": ["postgres", "mysql"], "expiresInSeconds": 3600 }

// 201  — same shape as approvals, with "runStatus": "awaiting_input"
```

### `GET /api/agent/runs/{runId}/approvals/{approvalId}`

How the agent learns the outcome: **poll this**. There is deliberately no push channel into the
container.

```jsonc
// 200
{
  "approvalId": "01J…",
  "status": "pending",          // pending | approved | rejected | expired
  "runStatus": "awaiting_approval",
  "answer": "postgres",         // present once a question has been answered
  "note": "looks good",
  "decidedBy": "01J…",
  "decidedAt": "2026-08-05T12:31:20Z",
  "expiresAt": "2026-08-05T13:31:16Z"
}
```

An approval belonging to a different run returns `404`.

### `POST /api/agent/runs/{runId}/usage`

Reports incremental cost. The delta is added to `runs.budget_used_usd` atomically in SQL, so
concurrent reports never lose an increment.

```jsonc
// request
{ "costUsd": 2.50, "tokensIn": 1000, "tokensOut": 400, "model": "some-model" }

// 200
{ "budgetUsedUsd": 2.50, "budgetMaxUsd": 5.00, "budgetExceeded": false }
```

When a report pushes the total past `budget_max_usd`, the host stops the container and transitions
the run to `budget_exceeded` (terminal). The response still returns `200` with
`"budgetExceeded": true` so a well-behaved agent can wind down gracefully instead of being killed
mid-write.

Related caps, enforced outside this endpoint:

* A caller-supplied `budgetMaxUsd` at run creation cannot exceed the manifest's
  `spec.budget.hardMaxUsd` (`422` otherwise).
* A run is refused (`422`) if the project's `budget_monthly_usd` is already exhausted for the
  current calendar month.

## 4. Run lifecycle from the agent's point of view

```
container starts
  │  read AGENTHOST_INPUTS, AGENTHOST_RUN_TOKEN
  │
  ├─ POST /events            … as often as it likes (progress, logs, tool calls)
  ├─ POST /usage             … after each billable call
  │
  ├─ POST /approvals         … run becomes awaiting_approval
  │   └─ GET  /approvals/{id}  poll until status != pending
  │        ├ approved → continue (run is back to running)
  │        ├ rejected → run is terminal (rejected); stop working
  │        └ expired  → run is terminal (timed_out); stop working
  │
  ├─ POST /questions         … run becomes awaiting_input; poll the same way
  │
  ├─ POST /outputs           … publish results
  └─ exit 0 (success) / non-zero (failure)
```

The host owns the terminal transition: exit code 0 → `succeeded`, non-zero → `failed`. The agent
never sets the run's status directly.

Deadlines the host enforces without being asked:

* **Max duration** — a background watchdog (every `Watchdog:IntervalSeconds`, default 30s) stops
  any container still running past `started_at + spec.runtime.maxDurationSeconds` and moves the run
  to `timed_out`.
* **Approval expiry** — a pending approval past `expires_at` is marked `expired` and its run moves
  to `timed_out`.
* **Budget** — see above.

On every terminal transition the run is appended to the project's memory (`run_history`), which
feeds pattern detection.

## 5. Minimal agent example

```bash
#!/usr/bin/env bash
set -euo pipefail

API="${AGENTHOST_API_URL:-http://host.docker.internal:8080}"
AUTH=(-H "Authorization: Bearer $AGENTHOST_RUN_TOKEN" -H "Content-Type: application/json")

event() {
  curl -sf "${AUTH[@]}" -X POST "$API/api/agent/runs/$AGENTHOST_RUN_ID/events" \
    -d "{\"eventType\":\"$1\",\"level\":\"info\",\"message\":\"$2\"}" > /dev/null
}

event "phase.started" "analysing inputs"

# ask a human before doing anything destructive
approval=$(curl -sf "${AUTH[@]}" -X POST "$API/api/agent/runs/$AGENTHOST_RUN_ID/approvals" \
  -d '{"prompt":"May I push to the default branch?"}' | jq -r .approvalId)

while true; do
  status=$(curl -sf "${AUTH[@]}" "$API/api/agent/runs/$AGENTHOST_RUN_ID/approvals/$approval" | jq -r .status)
  [ "$status" = "pending" ] || break
  sleep 5
done
[ "$status" = "approved" ] || { event "phase.aborted" "not approved"; exit 1; }

curl -sf "${AUTH[@]}" -X POST "$API/api/agent/runs/$AGENTHOST_RUN_ID/usage" \
  -d '{"costUsd":0.42,"model":"some-model"}' > /dev/null

curl -sf "${AUTH[@]}" -X POST "$API/api/agent/runs/$AGENTHOST_RUN_ID/outputs" \
  -d '{"outputs":{"filesChanged":3}}' > /dev/null

event "phase.completed" "done"
```
