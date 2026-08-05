import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { RunDetailComponent } from './run-detail.component';
import { RunService } from '../../services/run.service';
import { AuthService } from '../../services/auth.service';
import { Approval, Run, User, UserRole } from '../../core/models';

function user(role: UserRole): User {
  return {
    id: 'u1',
    orgId: 'o1',
    email: 'someone@example.com',
    displayName: null,
    avatarUrl: null,
    role,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

function approval(overrides: Partial<Approval> = {}): Approval {
  return {
    id: 'a1',
    runId: 'r1',
    stepId: 'write-files',
    approvalType: 'gate',
    prompt: 'May I write to the repository?',
    options: null,
    requiredRole: 'maintainer',
    requiredCount: 1,
    responses: [],
    status: 'pending',
    expiresAt: '2026-01-01T01:00:00Z',
    decidedAt: null,
    decidedBy: null,
    createdAt: '2026-01-01T00:00:00Z',
    ...overrides,
  };
}

function run(status: Run['status']): Run {
  return {
    id: 'r1',
    orgId: 'o1',
    projectId: 'p1',
    number: 1,
    agentId: 'ag1',
    agentVersionId: 'v1',
    status,
    inputs: {},
    context: {},
    outputs: null,
    durationMs: null,
    exitCode: null,
    errorMessage: null,
    errorCode: null,
    budgetMaxUsd: null,
    budgetUsedUsd: null,
    triggeredByUserId: 'u1',
    triggeredByType: 'manual',
    parentRunId: null,
    rootRunId: null,
    createdAt: '2026-01-01T00:00:00Z',
    startedAt: null,
    finishedAt: null,
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('RunDetailComponent approval gating', () => {
  let component: RunDetailComponent;
  let runService: RunService;
  let authService: AuthService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [RunDetailComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
      ],
    });

    runService = TestBed.inject(RunService);
    authService = TestBed.inject(AuthService);
    // The component never gets a route id here, so nothing is fetched; guard the effect anyway.
    vi.spyOn(runService, 'fetchRun').mockResolvedValue();

    component = TestBed.createComponent(RunDetailComponent).componentInstance;
  });

  afterEach(() => vi.restoreAllMocks());

  function blockOn(status: Run['status'], pending: Approval): void {
    runService.runs.set([run(status)]);
    runService.currentRunId.set('r1');
    component.approvals.set([pending]);
  }

  it('marks the run blocked and surfaces the pending approval', () => {
    blockOn('awaiting_approval', approval());

    expect(component.isBlocked()).toBe(true);
    expect(component.isAwaitingApproval()).toBe(true);
    expect(component.pendingApproval()?.prompt).toBe('May I write to the repository?');
    expect(component.requiredRole()).toBe('maintainer');
  });

  it('refuses to offer a decision the server would reject', () => {
    blockOn('awaiting_approval', approval({ requiredRole: 'maintainer' }));

    authService.currentUser.set(user('developer'));
    expect(component.canDecide()).toBe(false);

    authService.currentUser.set(user('maintainer'));
    expect(component.canDecide()).toBe(true);

    authService.currentUser.set(user('owner'));
    expect(component.canDecide()).toBe(true);
  });

  it('falls back to the developer role the server defaults to', () => {
    blockOn('awaiting_approval', approval({ requiredRole: null }));

    authService.currentUser.set(user('viewer'));
    expect(component.requiredRole()).toBe('developer');
    expect(component.canDecide()).toBe(false);

    authService.currentUser.set(user('developer'));
    expect(component.canDecide()).toBe(true);
  });

  it('does nothing when an under-privileged user forces a decision', async () => {
    blockOn('awaiting_approval', approval({ requiredRole: 'owner' }));
    authService.currentUser.set(user('developer'));
    const approveRun = vi.spyOn(runService, 'approveRun').mockResolvedValue();

    await component.decide('approve');

    expect(approveRun).not.toHaveBeenCalled();
  });

  it('sends the decision with the approval step id and no user id', async () => {
    blockOn('awaiting_approval', approval());
    authService.currentUser.set(user('owner'));
    const approveRun = vi.spyOn(runService, 'approveRun').mockResolvedValue();
    vi.spyOn(runService, 'fetchApprovals').mockResolvedValue([]);
    component.approveNote.set('looks fine');

    await component.decide('reject');

    expect(approveRun).toHaveBeenCalledWith('r1', {
      stepId: 'write-files',
      decision: 'reject',
      note: 'looks fine',
    });
  });

  it('answers a question using the approval id as the question id', async () => {
    blockOn('awaiting_input', approval({ approvalType: 'question', options: ['yes', 'no'] }));
    authService.currentUser.set(user('maintainer'));
    const answer = vi.spyOn(runService, 'answerQuestion').mockResolvedValue();
    vi.spyOn(runService, 'fetchApprovals').mockResolvedValue([]);

    expect(component.isAwaitingInput()).toBe(true);
    expect(component.approvalOptions()).toEqual(['yes', 'no']);

    component.selectOption('yes');
    await component.submitAnswer();

    expect(answer).toHaveBeenCalledWith('r1', 'a1', 'yes');
  });

  it('treats non-list options as free text', () => {
    blockOn('awaiting_input', approval({ options: { shape: 'object' } }));
    expect(component.approvalOptions()).toEqual([]);
  });

  it('has no pending approval once the run is running again', () => {
    blockOn('running', approval({ status: 'approved' }));

    expect(component.isBlocked()).toBe(false);
    expect(component.pendingApproval()).toBeNull();
  });
});
