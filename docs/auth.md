# AgentHost authentication

Who can get a token, what each kind of token is allowed to do, and which parts of this are not
production-ready yet.

---

## 1. Token model

Six distinct credentials exist. They are deliberately not interchangeable — each one is either a
different *kind* of thing (an opaque random string versus a JWT) or a JWT with a different audience,
so presenting one where another is expected fails.

| Token | Form | Lifetime | Revocable | What it authorizes |
| --- | --- | --- | --- | --- |
| **Access token** | JWT, `aud: agenthost`, claims `sub` / `org_id` / `email` / `role` | 15 min (`Jwt:ExpiryMinutes`) | No | Every authenticated API call and hub connection |
| **Refresh token** | Opaque, 256-bit random | 14 days, rotated on use | Yes | Minting a new access+refresh pair at `POST /api/auth/refresh` |
| **Invitation token** | Opaque, 256-bit random | 7 days, single-use | Yes (revoke) | Creating one user, in the inviting org, at the invited role |
| **Password-reset token** | Opaque, 256-bit random | 30 min, single-use | Yes | Setting one user's password |
| **MFA challenge token** | JWT, `aud: agenthost-mfa`, `token_type: mfa_challenge` | 5 min | No (short TTL) | Exactly one call: `POST /api/auth/mfa/verify` |
| **Agent run token** | JWT, `aud: agenthost-agent`, `token_type: agent_run` | run duration + margin | No | One run's callbacks — see [agent-protocol.md](agent-protocol.md) |

### Storage

Every opaque token is stored **as a SHA-256 hash only**. The raw value is handed to its recipient
exactly once, in the response that creates it, and is not recoverable afterwards — losing an
invitation token means revoking it and issuing a new one. A read of the database therefore yields no
usable credentials. (A plain fast hash is right here: these values carry full 256-bit entropy, so
there is nothing for an offline attacker to guess and a slow KDF would only cost the request path.
Passwords, which *are* guessable, use PBKDF2-SHA256 at 210 000 iterations instead.)

The TOTP shared secret is the one exception to "hash it": TOTP verification needs the seed back, so
it is **encrypted** with AES-256-GCM (`Secrets:EncryptionKey`, the same broker the secrets table
uses) rather than hashed.

### Access tokens cannot be revoked — on purpose

JWTs are stateless and this deployment keeps no deny-list. Logging out, resetting a password,
deleting a user or disabling MFA all revoke *refresh* tokens immediately; an access token already in
someone's hands stays valid until it expires. The 15-minute TTL is the mitigation, and it is a
deliberate trade rather than an oversight. If that window is unacceptable for a deployment, shorten
`Jwt:ExpiryMinutes` or add a deny-list keyed on the `jti` claim, which every access token carries
for exactly that purpose.

### Why the MFA challenge token cannot act as an access token

This is the assertion the whole MFA feature rests on, so it is worth being explicit. Three
independent things stop it, any one of which would be sufficient:

1. **Audience.** The application's JWT bearer scheme validates `aud == Jwt:Audience` (`agenthost`).
   A challenge token's audience is `agenthost-mfa`, so presenting it as `Authorization: Bearer …`
   fails audience validation and the request is 401.
2. **`token_type` claim.** The challenge token carries `token_type: mfa_challenge`, which no access
   token has, and which `MfaChallengeTokenService.ValidateAndGetUserId` demands. The converse holds
   too: an access token has no such claim and is rejected at `/api/auth/mfa/verify`.
3. **No role claim.** Even if it somehow authenticated, it would satisfy no role policy.

`Integration/MfaLoginTests.cs` asserts both directions — a challenge token against `/api/auth/me`,
`/api/users`, `/api/projects`, `/api/auth/logout` and `/api/auth/refresh`, and an access token
against `/api/auth/mfa/verify`. This is the same audience-separation trick the agent run token uses.

---

## 2. Endpoints

### Anonymous

These are reachable without any credential, because the caller by definition does not have one yet.

| Endpoint | Notes |
| --- | --- |
| `POST /api/auth/register` | Creates a **new org** + its owner. 403 when `Auth:AllowSelfRegistration` is false. |
| `POST /api/auth/login` | Email + password. Returns a session, *or* an MFA challenge (below). |
| `POST /api/auth/refresh` | Rotates a refresh token. Replaying a revoked one kills the whole token family (reuse detection). |
| `POST /api/auth/mfa/verify` | Challenge token + TOTP or recovery code → real session. |
| `POST /api/auth/password-reset/request` | **Always 202**, existing address or not. |
| `POST /api/auth/password-reset/confirm` | Token + new password. |
| `POST /api/invitations/accept` | Token + a password the invitee chooses. |

### Authenticated

| Endpoint | Role required |
| --- | --- |
| `GET /api/auth/me`, `POST /api/auth/logout` | any |
| `POST /api/auth/password` | any (own password; requires the current one) |
| `POST /api/auth/mfa/enroll` / `confirm` / `disable` | any (own account) |
| `POST /api/invitations`, `GET /api/invitations`, `DELETE /api/invitations/{id}` | Maintainer+ |
| `POST /api/users`, `PUT /api/users/{id}`, `DELETE /api/users/{id}` | Maintainer+ |

Everything in the invitation and user surface is scoped to the caller's **own** organization, taken
from their token. An invitation issued in org A creates a user in org A no matter who redeems it or
what they send; another tenant's invitation reads as 404, never 403.

### Login with MFA enabled

```
POST /api/auth/login          → { "mfaRequired": true, "mfaToken": "…", "mfaExpiresInSeconds": 300 }
POST /api/auth/mfa/verify     → { "token": "…", "refreshToken": "…", "user": { … } }
```

When MFA is enabled a correct password yields **no access token and no refresh token**. For accounts
without MFA the login response is unchanged apart from `mfaRequired: false`.

Enrollment is two-phase (`/enroll` writes the secret disabled, `/confirm` proves a live code and
enables it) so a mis-scanned QR code cannot lock a user out of their own account. Confirmation
returns 10 single-use recovery codes, once. Disabling requires a TOTP or recovery code — an access
token alone must not be able to strip the second factor off an account.

### Uniform failures

Invitation acceptance, reset confirmation and MFA verification answer the same way for *every*
failure — unknown token, expired token, revoked token, already-used token, wrong code. Distinguishing
them would turn each endpoint into an oracle for probing which tokens exist. Likewise the reset
*request* endpoint answers 202 identically whether or not the email has an account, so it cannot be
used to enumerate the user directory.

---

## 3. Password policy

Applied at all six places a password is set — registration, `POST /api/users`, `PUT /api/users/{id}`,
invitation acceptance, reset confirmation, and password change:

- at least 12 characters;
- not a single repeated character;
- not on the embedded common-password blocklist;
- optionally, not present in Have I Been Pwned's breach corpus.

The breach check is **off by default** (`Auth:BreachedPasswordCheck:Enabled`). It uses the
k-anonymity range API: only the first five hex characters of the password's SHA-1 leave the process
— never the password, never its full hash — and the returned bucket of suffixes is compared locally.
It has a 2-second timeout and **fails open**: any DNS, TLS, timeout, 5xx or parse failure logs a
warning and treats the password as not breached. An unreachable third party must never be able to
block a legitimate signup; the check strengthens the policy and is not permitted to become a
dependency of it.

---

## 4. What is not production-ready

Two honest caveats. Both come down to the same missing piece: **this system has no mailer.**

### 4.1 Invitations are delivered by hand

`POST /api/invitations` returns the raw token in its response and sends nothing to anybody. The
inviter has to pass it to the invitee over some channel they trust. The endpoint's documentation says
so plainly rather than implying an email went out. This is workable but manual, and the token's
security now depends on whatever channel the humans pick.

### 4.2 Password reset is a placeholder in production

`POST /api/auth/password-reset/request` mints and stores a token, and then, in any default or
production configuration, **discards the raw value** — nothing reaches the user. The reset flow is
therefore inert until a mailer exists. That is a known, deliberate gap, not a bug to be worked
around.

`Auth:ReturnResetTokenInResponse` (default `false`) makes the endpoint return the raw token to the
caller instead. That exists so development and integration tests can drive the flow end to end.
**Never enable it in production**: the endpoint is anonymous, so anyone who can name an email address
could take over that account. The service logs a warning at startup when it is on.

### 4.3 Other known limits

- **No deny-list for access tokens** (§1) — revocation is refresh-token-shaped, bounded by the
  15-minute access token TTL.
- **MFA is per-user opt-in.** There is no org-level policy that can require it, and no admin path to
  reset a second factor for a user who has lost both their authenticator and their recovery codes —
  that currently needs a database change.
- **No lockout after repeated failed MFA or password attempts.** The global rate limiter
  (20 requests/minute per client on `/api/auth/*`) is the only brake.
- **TOTP replay within a step is not prevented.** A code stays valid for its 30-second window plus
  one step of drift either side, and a used code is not remembered. Recovery codes *are* single-use.
