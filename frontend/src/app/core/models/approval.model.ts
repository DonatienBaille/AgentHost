import { UserRole } from './auth.model';

/** Backend Domain/Enums.cs, serialized with the snake_case DB strings. */
export type ApprovalType = 'gate' | 'question' | 'budget_increase';
export type ApprovalStatus = 'pending' | 'approved' | 'rejected' | 'expired';

/** One recorded decision on an approval (backend Domain/Approval.cs, ApprovalResponse). */
export interface ApprovalResponseEntry {
  by: string;
  /** approve | reject | answer */
  decision: string;
  at: string;
  note: string | null;
  answer: string | null;
}

/**
 * A human-in-the-loop gate or question raised by an agent, as returned by
 * GET /api/runs/{runId}/approvals (backend Endpoints/ApprovalEndpoints.cs).
 */
export interface Approval {
  id: string;
  runId: string;
  stepId: string | null;
  approvalType: ApprovalType;
  /** Text the agent wants a human to act on. */
  prompt: string;
  /**
   * Free-form JSON supplied by the agent (`JsonNode?`). In practice a list of allowed answers;
   * anything else is rendered as free text input instead. See approvalOptions() in run-detail.
   */
  options: unknown | null;
  /** Minimum role allowed to decide. The server enforces this too and rejects under-privileged callers. */
  requiredRole: UserRole | null;
  requiredCount: number;
  responses: ApprovalResponseEntry[];
  status: ApprovalStatus;
  expiresAt: string;
  decidedAt: string | null;
  decidedBy: string | null;
  createdAt: string;
}
