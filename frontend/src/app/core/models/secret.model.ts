export type SecretScope = 'org' | 'project';

/**
 * Secret metadata only — the API never returns the encrypted/plaintext value,
 * see backend Endpoints/SecretEndpoints.cs.
 */
export interface Secret {
  id: string;
  orgId: string;
  projectId: string | null;
  name: string;
  scope: SecretScope;
  lastUsedAt: string | null;
  createdAt: string;
}

export interface CreateSecretRequest {
  orgId: string;
  projectId?: string;
  name: string;
  value: string;
  scope: SecretScope;
}
