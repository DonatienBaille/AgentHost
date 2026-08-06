import {
  StatusColor,
  approvalBadgeClass,
  statusBadgeClass,
  statusColor,
  statusTextClass,
} from './status';
import { RunStatus } from '../models';

/**
 * Exhaustive by construction: this is a Record keyed by RunStatus, so adding a status
 * server-side (and to the RunStatus union) makes this file fail to compile until the new status
 * is given an expected colour — instead of silently rendering unstyled.
 */
const EXPECTED: Record<RunStatus, StatusColor> = {
  pending: 'gray',
  queued: 'gray',
  provisioning: 'gray',
  preparing: 'gray',
  running: 'yellow',
  awaiting_approval: 'yellow',
  awaiting_input: 'yellow',
  finalizing: 'gray',
  succeeded: 'green',
  failed: 'red',
  cancelled: 'gray',
  timed_out: 'red',
  budget_exceeded: 'red',
  rejected: 'red',
  infra_error: 'red',
};

const ALL_STATUSES = Object.keys(EXPECTED) as RunStatus[];

describe('statusColor', () => {
  it('maps every RunStatus to its documented colour', () => {
    const actual = Object.fromEntries(ALL_STATUSES.map((s) => [s, statusColor(s)]));
    expect(actual).toEqual(EXPECTED);
  });

  it('falls back to gray for absent or unknown statuses', () => {
    expect(statusColor(null)).toBe('gray');
    expect(statusColor(undefined)).toBe('gray');
    expect(statusColor('')).toBe('gray');
    expect(statusColor('not_a_status')).toBe('gray');
  });

  it('is case-sensitive — an upper-cased status is not recognised', () => {
    expect(statusColor('SUCCEEDED')).toBe('gray');
  });
});

describe('statusTextClass / statusBadgeClass', () => {
  it('returns a non-empty class for every RunStatus', () => {
    for (const status of ALL_STATUSES) {
      expect(statusTextClass(status).length).toBeGreaterThan(0);
      expect(statusBadgeClass(status).length).toBeGreaterThan(0);
    }
  });

  it('agrees with statusColor: same colour implies same classes', () => {
    const byColor = new Map<StatusColor, { text: string; badge: string }>();
    for (const status of ALL_STATUSES) {
      const color = statusColor(status);
      const classes = { text: statusTextClass(status), badge: statusBadgeClass(status) };
      const seen = byColor.get(color);
      if (seen) {
        expect(classes).toEqual(seen);
      } else {
        byColor.set(color, classes);
      }
    }
    // All four colours are reachable from the RunStatus set.
    expect([...byColor.keys()].sort()).toEqual(['gray', 'green', 'red', 'yellow']);
  });

  it('uses distinct classes per colour', () => {
    const texts = new Set(
      (['green', 'red', 'yellow', 'gray'] as StatusColor[]).map((c) =>
        statusTextClass(c === 'green' ? 'succeeded' : c === 'red' ? 'failed' : c === 'yellow' ? 'running' : 'queued'),
      ),
    );
    expect(texts.size).toBe(4);
  });

  it('falls back to the gray classes for an unknown status', () => {
    expect(statusTextClass('nope')).toBe(statusTextClass('queued'));
    expect(statusBadgeClass('nope')).toBe(statusBadgeClass('queued'));
  });
});

describe('approvalBadgeClass', () => {
  it('maps the approval vocabulary to its own colours', () => {
    expect(approvalBadgeClass('approved')).toBe(statusBadgeClass('succeeded'));
    expect(approvalBadgeClass('rejected')).toBe(statusBadgeClass('failed'));
    expect(approvalBadgeClass('expired')).toBe(statusBadgeClass('failed'));
    expect(approvalBadgeClass('pending')).toBe(statusBadgeClass('running'));
  });

  it('falls back to gray for absent or unknown approval statuses', () => {
    expect(approvalBadgeClass(null)).toBe(statusBadgeClass('queued'));
    expect(approvalBadgeClass(undefined)).toBe(statusBadgeClass('queued'));
    expect(approvalBadgeClass('whatever')).toBe(statusBadgeClass('queued'));
  });

  // 'pending' means yellow for approvals but gray for runs — the two vocabularies differ.
  it('treats pending differently from the run status of the same name', () => {
    expect(approvalBadgeClass('pending')).not.toBe(statusBadgeClass('pending'));
  });
});
