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

export interface CreateRunRequest {
  agentId: string;
  inputs: Record<string, unknown>;
  context?: Record<string, unknown>;
}

export type ApprovalDecision = 'approve' | 'reject';

export interface ApprovalRequest {
  stepId: string;
  decision: ApprovalDecision;
  note?: string;
}
