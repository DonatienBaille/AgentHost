export type RunStatus =
  | 'pending'
  | 'queued'
  | 'provisioning'
  | 'preparing'
  | 'running'
  | 'awaiting_approval'
  | 'awaiting_input'
  | 'finalizing'
  | 'succeeded'
  | 'failed'
  | 'cancelled'
  | 'timed_out'
  | 'budget_exceeded'
  | 'rejected'
  | 'infra_error';

export type TriggeredByType = 'manual' | 'webhook' | 'cron' | 'api' | 'chain';

export interface Run {
  id: string;
  orgId: string;
  projectId: string;
  number: number;
  agentId: string;
  agentVersionId: string;
  status: RunStatus;
  inputs: Record<string, unknown>;
  context: Record<string, unknown>;
  outputs: Record<string, unknown> | null;
  durationMs: number | null;
  exitCode: number | null;
  errorMessage: string | null;
  errorCode: string | null;
  budgetMaxUsd: number | null;
  budgetUsedUsd: number | null;
  triggeredByUserId: string | null;
  triggeredByType: TriggeredByType | null;
  parentRunId: string | null;
  rootRunId: string | null;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  updatedAt: string;
}

/**
 * No triggeredByUserId: the actor is taken from the caller's JWT
 * (backend Contracts/RunContracts.cs).
 */
export interface CreateRunRequest {
  agentId: string;
  inputs: Record<string, unknown>;
  context?: Record<string, unknown>;
  budgetMaxUsd?: number;
}

export type ApprovalDecision = 'approve' | 'reject';

/** POST /api/runs/{id}/approve. No decidedByUserId — the server reads the decider from the JWT. */
export interface ApprovalRequest {
  stepId?: string;
  decision: ApprovalDecision;
  note?: string;
}

/** POST /api/runs/{id}/answer. No answeredByUserId — same reason as ApprovalRequest. */
export interface AnswerQuestionRequest {
  questionId: string;
  answer: string;
}

/**
 * Un nœud de l'arbre de chaînage (feuille de route, lot 4).
 *
 * `agentName` est résolu par le serveur : un identifiant seul n'apprend rien à qui lit l'arbre, et
 * l'IHM ne peut pas résoudre trente identifiants sans trente appels.
 */
export interface RunTreeNode {
  id: string;
  number: number;
  agentId: string;
  agentName: string | null;
  status: string;
  triggeredByType: string;
  parentRunId: string | null;
  chainDepth: number;
  budgetUsedUsd: number | null;
  durationMs: number | null;
  createdAt: string;
  children: RunTreeNode[];
}

/**
 * L'arbre complet auquel un run appartient, et ses totaux.
 *
 * Les totaux sont la question qu'on se pose devant une cascade : ce que l'ensemble a coûté, pas ce
 * qu'a coûté le maillon qu'on regarde.
 */
export interface RunTree {
  rootRunId: string;
  root: RunTreeNode | null;
  totalRuns: number;
  totalBudgetUsedUsd: number;
  maxDepth: number;
}
