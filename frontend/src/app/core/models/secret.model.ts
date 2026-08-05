export type SecretScope = 'org' | 'project';

/**
 * Metadata only. Every secret route projects through the backend's `SecretResponse`
 * (Contracts/SecretContracts.cs), which never carries the plaintext, the ciphertext or the vault
 * path — there is deliberately no `value` field to read back.
 */
export interface Secret {
  id: string;
  orgId: string;
  projectId: string | null;
  name: string;
  scope: SecretScope;
  lastUsedAt: string | null;
  lastUsedByRunId: string | null;
  createdAt: string;
  updatedAt: string;
}

/** No orgId: derived from the caller's JWT (Contracts/SecretContracts.cs). */
export interface CreateSecretRequest {
  projectId?: string;
  name: string;
  value: string;
  scope: SecretScope;
}

/** PUT /api/secrets/{id} — rotates the stored value in place. */
export interface UpdateSecretRequest {
  value: string;
}
