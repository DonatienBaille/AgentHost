/**
 * Les agrégats métier servis par `/api/metrics` (backend Endpoints/MetricsEndpoints.cs).
 *
 * La portée vient toujours du JWT : aucun de ces appels ne porte d'identifiant d'organisation, il
 * n'y a donc rien à falsifier côté client.
 */

export interface MetricsOverview {
  windowDays: number;
  totalRuns: number;
  succeededRuns: number;
  /** Toute la famille des issues non réussies, erreurs d'infra et budgets épuisés compris. */
  failedRuns: number;
  infraErrorRuns: number;
  budgetExceededRuns: number;
  inFlightRuns: number;
  costUsd: number;
  averageDurationMs: number;
  /**
   * L'attente de la plus ancienne approbation encore en suspens — un maximum, pas une moyenne :
   * une moyenne basse masquerait la demande oubliée, qui est justement celle à voir.
   */
  oldestPendingApprovalSeconds: number;
}

export interface AgentUsage {
  agentId: string;
  name: string;
  runs: number;
  succeeded: number;
  costUsd: number;
  averageDurationMs: number;
}

export interface ProjectUsage {
  projectId: string;
  name: string;
  runs: number;
  succeeded: number;
  costUsd: number;
  /** Plafond mensuel du projet. Null = aucun plafond fixé, donc rien à dépasser. */
  budgetMonthlyUsd: number | null;
}

export interface DailyPoint {
  day: string;
  runs: number;
  succeeded: number;
  costUsd: number;
}
