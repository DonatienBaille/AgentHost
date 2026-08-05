/** Canonical webhook event names (must match the backend's webhook dispatcher). */
export const WEBHOOK_EVENTS = [
  'run.created',
  'run.succeeded',
  'run.failed',
  'run.cancelled',
  'run.timed_out',
  'run.budget_exceeded',
  'run.rejected',
  'run.infra_error',
  'run.finished',
  'approval.requested',
] as const;

export type WebhookEvent = (typeof WEBHOOK_EVENTS)[number];

export interface Webhook {
  id: string;
  projectId: string;
  url: string;
  events: string[];
  secretToken: string | null;
  isActive: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface CreateWebhookRequest {
  projectId: string;
  url: string;
  events: string[];
  secretToken?: string;
}

export interface UpdateWebhookRequest {
  isActive?: boolean;
  events?: string[];
  url?: string;
}
