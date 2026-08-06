import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { RunService } from './run.service';
import { environment } from '../../environments/environment';
import { Run, RunStatus } from '../core/models';

const URL = `${environment.apiUrl}/api/runs`;

function run(id: string, status: RunStatus = 'running'): Run {
  return {
    id,
    orgId: 'o1',
    projectId: 'p1',
    number: 1,
    agentId: 'a1',
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
    triggeredByUserId: null,
    triggeredByType: 'manual',
    parentRunId: null,
    rootRunId: null,
    createdAt: '2026-01-01T00:00:00Z',
    startedAt: null,
    finishedAt: null,
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

/** Lets the service's own follow-up request (the post-action refetch) reach the mock backend. */
const tick = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

describe('RunService', () => {
  let service: RunService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(RunService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  describe('listRuns', () => {
    it('defaults to skip=0 and take=50', async () => {
      const pending = service.listRuns();
      expect(service.isLoading()).toBe(true);

      const req = httpMock.expectOne(`${URL}?skip=0&take=50`);
      expect(req.request.method).toBe('GET');
      req.flush([run('r1')]);

      await pending;
      expect(service.runCount()).toBe(1);
      expect(service.error()).toBeNull();
      expect(service.isLoading()).toBe(false);
    });

    it('forwards an explicit paging window verbatim', async () => {
      const pending = service.listRuns(100, 25);
      httpMock.expectOne(`${URL}?skip=100&take=25`).flush([]);
      await pending;
      expect(service.runs()).toEqual([]);
    });

    // The server clamps take to 200 silently. Clamping here too keeps the requested page size
    // equal to the delivered one, so a caller advancing by `take` cannot skip rows it never saw.
    it('clamps take to the servers 200 cap before sending', async () => {
      const pending = service.listRuns(0, 1000);
      httpMock.expectOne(`${URL}?skip=0&take=200`).flush([]);
      await pending;
    });

    it('floors a negative skip and a non-positive take', async () => {
      const pending = service.listRuns(-10, 0);
      httpMock.expectOne(`${URL}?skip=0&take=1`).flush([]);
      await pending;
    });

    it('sets the error signal and stops loading on failure', async () => {
      const pending = service.listRuns();
      httpMock.expectOne(`${URL}?skip=0&take=50`).flush(null, { status: 500, statusText: 'Err' });
      await pending;

      expect(service.error()).toBe('errors.loadRuns');
      expect(service.isLoading()).toBe(false);
    });
  });

  describe('createRun payload', () => {
    // REGRESSION: the server reads the triggering user from the JWT. If triggeredByUserId or
    // orgId is reintroduced into the create body, this must fail.
    it('never sends triggeredByUserId or orgId in the body', async () => {
      const pending = service.createRun({ agentId: 'a1', inputs: { x: 1 } });

      const req = httpMock.expectOne(URL);
      expect(req.request.method).toBe('POST');
      const keys = Object.keys(req.request.body as object);
      expect(keys).not.toContain('triggeredByUserId');
      expect(keys).not.toContain('triggeredBy');
      expect(keys).not.toContain('userId');
      expect(keys).not.toContain('orgId');
      expect(keys).not.toContain('organizationId');
      expect(req.request.body).toEqual({ agentId: 'a1', inputs: { x: 1 } });

      req.flush(run('r1'));
      expect((await pending).id).toBe('r1');
      expect(service.runs().length).toBe(1);
      expect(service.error()).toBeNull();
    });

    it('carries optional context and budget when supplied, and nothing else', async () => {
      const pending = service.createRun({
        agentId: 'a1',
        inputs: {},
        context: { branch: 'main' },
        budgetMaxUsd: 5,
      });
      const req = httpMock.expectOne(URL);
      expect(Object.keys(req.request.body as object).sort()).toEqual([
        'agentId',
        'budgetMaxUsd',
        'context',
        'inputs',
      ]);
      req.flush(run('r1'));
      await pending;
    });

    it('rethrows and records the error when creation fails', async () => {
      const pending = service.createRun({ agentId: 'a1', inputs: {} });
      httpMock.expectOne(URL).flush(null, { status: 402, statusText: 'Payment Required' });

      await expect(pending).rejects.toBeTruthy();
      expect(service.error()).toBe('errors.createRun');
      expect(service.isLoading()).toBe(false);
      expect(service.runs()).toEqual([]);
    });
  });

  describe('approveRun payload', () => {
    // REGRESSION: no decidedByUserId — the decider comes from the JWT.
    it('never sends decidedByUserId in the approve body', async () => {
      const pending = service.approveRun('r1', { decision: 'approve', note: 'ok', stepId: 's1' });

      const req = httpMock.expectOne(`${URL}/r1/approve`);
      expect(req.request.method).toBe('POST');
      const keys = Object.keys(req.request.body as object);
      expect(keys).not.toContain('decidedByUserId');
      expect(keys).not.toContain('decidedBy');
      expect(keys).not.toContain('userId');
      expect(keys).not.toContain('orgId');
      expect(req.request.body).toEqual({ decision: 'approve', note: 'ok', stepId: 's1' });
      req.flush(null);

      // The service refetches the run after deciding.
      await tick();
      httpMock.expectOne(`${URL}/r1`).flush(run('r1', 'running'));
      await pending;
      expect(service.runs()[0].id).toBe('r1');
    });

    it('rejects with only a decision when no note is given', async () => {
      const pending = service.approveRun('r1', { decision: 'reject' });
      const req = httpMock.expectOne(`${URL}/r1/approve`);
      expect(Object.keys(req.request.body as object)).toEqual(['decision']);
      req.flush(null);
      await tick();
      httpMock.expectOne(`${URL}/r1`).flush(run('r1', 'rejected'));
      await pending;
      expect(service.runs()[0].status).toBe('rejected');
    });

    it('rethrows and records the error when approving fails', async () => {
      const pending = service.approveRun('r1', { decision: 'approve' });
      httpMock.expectOne(`${URL}/r1/approve`).flush(null, { status: 403, statusText: 'Forbidden' });

      await expect(pending).rejects.toBeTruthy();
      expect(service.error()).toBe('errors.approveRun');
    });
  });

  describe('answerQuestion payload', () => {
    // REGRESSION: no answeredByUserId — the answerer comes from the JWT.
    it('sends exactly questionId and answer, never answeredByUserId', async () => {
      const pending = service.answerQuestion('r1', 'q1', '42');

      const req = httpMock.expectOne(`${URL}/r1/answer`);
      expect(req.request.method).toBe('POST');
      const keys = Object.keys(req.request.body as object);
      expect(keys.sort()).toEqual(['answer', 'questionId']);
      expect(keys).not.toContain('answeredByUserId');
      expect(keys).not.toContain('userId');
      expect(keys).not.toContain('orgId');
      req.flush(null);

      await tick();
      httpMock.expectOne(`${URL}/r1`).flush(run('r1', 'running'));
      await pending;
    });

    it('rethrows and records the error when answering fails', async () => {
      const pending = service.answerQuestion('r1', 'q1', '42');
      httpMock.expectOne(`${URL}/r1/answer`).flush(null, { status: 409, statusText: 'Conflict' });

      await expect(pending).rejects.toBeTruthy();
      expect(service.error()).toBe('errors.answerQuestion');
    });
  });

  describe('cancelRun', () => {
    it('POSTs an empty body and refetches the run', async () => {
      const pending = service.cancelRun('r1');
      const req = httpMock.expectOne(`${URL}/r1/cancel`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({});
      req.flush(null);

      await tick();
      httpMock.expectOne(`${URL}/r1`).flush(run('r1', 'cancelled'));
      await pending;
      expect(service.runs()[0].status).toBe('cancelled');
    });

    it('rethrows and records the error when cancelling fails', async () => {
      const pending = service.cancelRun('r1');
      httpMock.expectOne(`${URL}/r1/cancel`).flush(null, { status: 500, statusText: 'Err' });

      await expect(pending).rejects.toBeTruthy();
      expect(service.error()).toBe('errors.cancelRun');
    });
  });

  describe('reads', () => {
    it('fetchRun upserts instead of duplicating', async () => {
      const first = service.fetchRun('r1');
      httpMock.expectOne(`${URL}/r1`).flush(run('r1', 'running'));
      await first;

      const second = service.fetchRun('r1');
      httpMock.expectOne(`${URL}/r1`).flush(run('r1', 'succeeded'));
      await second;

      expect(service.runs().length).toBe(1);
      expect(service.runs()[0].status).toBe('succeeded');
    });

    it('fetchRun records the failing id', async () => {
      const pending = service.fetchRun('r9');
      httpMock.expectOne(`${URL}/r9`).flush(null, { status: 404, statusText: 'Not Found' });
      await pending;
      expect(service.error()).toBe('errors.loadRun');
    });

    it('fetchApprovals GETs the approvals sub-resource', async () => {
      const pending = service.fetchApprovals('r1');
      const req = httpMock.expectOne(`${URL}/r1/approvals`);
      expect(req.request.method).toBe('GET');
      req.flush([]);
      expect(await pending).toEqual([]);
    });

    it('fetchEvents defaults fromSeq to 0 and forwards an explicit cursor', async () => {
      const first = service.fetchEvents('r1');
      httpMock.expectOne(`${URL}/r1/events?fromSeq=0`).flush([]);
      await first;

      const second = service.fetchEvents('r1', 12);
      httpMock.expectOne(`${URL}/r1/events?fromSeq=12`).flush([]);
      await second;
    });

    it('fetchEvents rethrows and records the error', async () => {
      const pending = service.fetchEvents('r1');
      httpMock.expectOne(`${URL}/r1/events?fromSeq=0`).flush(null, {
        status: 500,
        statusText: 'Err',
      });

      await expect(pending).rejects.toBeTruthy();
      expect(service.error()).toBe('errors.loadRunEvents');
    });

    it('fetchLogs unwraps the logs envelope', async () => {
      const pending = service.fetchLogs('r1');
      const req = httpMock.expectOne(`${URL}/r1/logs`);
      expect(req.request.method).toBe('GET');
      req.flush({ logs: 'hello' });
      expect(await pending).toBe('hello');
    });
  });

  describe('derived signals', () => {
    async function seed(statuses: RunStatus[]): Promise<void> {
      const pending = service.listRuns();
      httpMock
        .expectOne(`${URL}?skip=0&take=50`)
        .flush(statuses.map((s, i) => run(`r${i}`, s)));
      await pending;
    }

    it('counts failed runs across every terminal failure status', async () => {
      await seed(['failed', 'rejected', 'timed_out', 'budget_exceeded', 'infra_error', 'succeeded']);
      expect(service.failedRuns()).toBe(5);
    });

    it('counts active runs across every in-flight status', async () => {
      await seed([
        'pending',
        'queued',
        'provisioning',
        'preparing',
        'running',
        'awaiting_approval',
        'awaiting_input',
        'finalizing',
        'succeeded',
        'cancelled',
      ]);
      expect(service.activeRuns()).toBe(8);
    });

    it('successRate is 0 with no runs and a percentage otherwise', async () => {
      expect(service.successRate()).toBe(0);
      await seed(['succeeded', 'succeeded', 'failed', 'cancelled']);
      expect(service.successRate()).toBe(50);
    });

    it('currentRun resolves the selected id, and applyRunState merges pushed state', async () => {
      await seed(['running']);
      expect(service.currentRun()).toBeNull();

      service.applyRunState(run('r0', 'succeeded'));
      expect(service.runs().length).toBe(1);
      expect(service.runs()[0].status).toBe('succeeded');
    });
  });
});
