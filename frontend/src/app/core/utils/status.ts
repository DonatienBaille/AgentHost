import { RunStatus } from '../models';

/**
 * Consistent status -> semantic color mapping used across the app.
 * succeeded = green, failed/rejected/timed_out/budget_exceeded/infra_error = red,
 * running/awaiting_approval/awaiting_input = yellow, everything else = gray.
 */
export type StatusColor = 'green' | 'red' | 'yellow' | 'gray';

const RED_STATUSES: ReadonlySet<string> = new Set([
  'failed',
  'rejected',
  'timed_out',
  'budget_exceeded',
  'infra_error',
]);

const YELLOW_STATUSES: ReadonlySet<string> = new Set([
  'running',
  'awaiting_approval',
  'awaiting_input',
]);

export function statusColor(status: RunStatus | string | null | undefined): StatusColor {
  if (!status) return 'gray';
  if (status === 'succeeded') return 'green';
  if (RED_STATUSES.has(status)) return 'red';
  if (YELLOW_STATUSES.has(status)) return 'yellow';
  return 'gray';
}

const TEXT_CLASS: Record<StatusColor, string> = {
  green: 'text-green-400',
  red: 'text-red-400',
  yellow: 'text-yellow-400',
  gray: 'text-gray-400',
};

const BADGE_CLASS: Record<StatusColor, string> = {
  green: 'bg-green-900/60 text-green-300 border border-green-700',
  red: 'bg-red-900/60 text-red-300 border border-red-700',
  yellow: 'bg-yellow-900/60 text-yellow-300 border border-yellow-700',
  gray: 'bg-gray-800 text-gray-300 border border-gray-700',
};

export function statusTextClass(status: RunStatus | string | null | undefined): string {
  return TEXT_CLASS[statusColor(status)];
}

export function statusBadgeClass(status: RunStatus | string | null | undefined): string {
  return BADGE_CLASS[statusColor(status)];
}

/**
 * Approval statuses use their own vocabulary (pending/approved/rejected/expired) and would
 * otherwise fall through to gray in statusColor().
 */
export function approvalBadgeClass(status: string | null | undefined): string {
  switch (status) {
    case 'approved':
      return BADGE_CLASS.green;
    case 'rejected':
    case 'expired':
      return BADGE_CLASS.red;
    case 'pending':
      return BADGE_CLASS.yellow;
    default:
      return BADGE_CLASS.gray;
  }
}
