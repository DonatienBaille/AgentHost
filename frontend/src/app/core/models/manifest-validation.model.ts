import { AgentType } from './agent.model';

/**
 * Réponse de `POST /api/agents/validate-manifest`
 * (backend `Contracts/AgentManifestValidationContracts.cs`).
 *
 * Le serveur répond 200 dans les deux cas : « ça ne parse pas encore » est la réponse attendue au
 * milieu d'une frappe, pas une erreur de requête. On distingue donc sur `valid`.
 */
export type ManifestValidation = ManifestValidationSuccess | ManifestValidationFailure;

export interface ManifestValidationSuccess {
  valid: true;
  /** Le manifeste tel que le parseur backend le normalise, défauts appliqués. */
  manifest: ParsedManifest;
  permissionExtensions: ManifestPermissionExtensions | null;
  /** Le document YAML entier projeté en JSON, aucune clé perdue, types des scalaires conservés. */
  document: unknown;
  error: null;
}

export interface ManifestValidationFailure {
  valid: false;
  manifest: null;
  permissionExtensions: null;
  document: null;
  error: ManifestValidationError;
}

/** `line`/`column` sont à base 1 et absents pour une erreur sans position (document vide, section manquante). */
export interface ManifestValidationError {
  message: string;
  line: number | null;
  column: number | null;
}

export interface ManifestPermissionExtensions {
  networkAllowlist: string[];
  writableRootfs: boolean;
}

/** Miroir de `Domain/AgentManifest.cs`. */
export interface ParsedManifest {
  apiVersion: string;
  kind: string;
  metadata: { name: string; displayName: string; description: string };
  spec: {
    type: AgentType;
    image: string | null;
    external: { provider: string; model: string | null; config: Record<string, string> } | null;
    inputs: Record<string, unknown>;
    outputs: Record<string, unknown> | null;
    permissions: { vcs: string; network: string; secrets: string[]; docker: boolean };
    runtime: { profile: string; cpu: number; memory: string; disk: string; maxDurationSeconds: number };
    budget: { defaultMaxUsd: number; hardMaxUsd: number };
    approvals: { beforeWrite: { requiredRole: string; requiredCount: number } | null } | null;
  };
}
