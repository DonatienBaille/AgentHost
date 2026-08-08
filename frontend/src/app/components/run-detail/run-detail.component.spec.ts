import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { Subject, of } from 'rxjs';
import { RunDetailComponent } from './run-detail.component';
import { RunService } from '../../services/run.service';
import { AuthService } from '../../services/auth.service';
import { ConnectionState, SignalRService } from '../../services/signalr.service';
import { ArtifactService } from '../../services/artifact.service';
import { Approval, Artifact, Run, RunEvent, UserRole } from '../../core/models';
import { approval, run, runEvent, runTree, runTreeNode, user } from '../../core/testing/fixtures';

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
    blockOn('awaiting_approval', approval('maintainer', { stepId: 'write-files' }));

    expect(component.isBlocked()).toBe(true);
    expect(component.isAwaitingApproval()).toBe(true);
    expect(component.pendingApproval()?.prompt).toBe('Déployer en production ?');
    expect(component.requiredRole()).toBe('maintainer');
  });

  it('refuses to offer a decision the server would reject', () => {
    blockOn('awaiting_approval', approval('maintainer'));

    authService.currentUser.set(user('developer'));
    expect(component.canDecide()).toBe(false);

    authService.currentUser.set(user('maintainer'));
    expect(component.canDecide()).toBe(true);

    authService.currentUser.set(user('owner'));
    expect(component.canDecide()).toBe(true);
  });

  it('falls back to the developer role the server defaults to', () => {
    blockOn('awaiting_approval', approval(null));

    authService.currentUser.set(user('viewer'));
    expect(component.requiredRole()).toBe('developer');
    expect(component.canDecide()).toBe(false);

    authService.currentUser.set(user('developer'));
    expect(component.canDecide()).toBe(true);
  });

  it('does nothing when an under-privileged user forces a decision', async () => {
    blockOn('awaiting_approval', approval('owner'));
    authService.currentUser.set(user('developer'));
    const approveRun = vi.spyOn(runService, 'approveRun').mockResolvedValue();

    await component.decide('approve');

    expect(approveRun).not.toHaveBeenCalled();
  });

  it('sends the decision with the approval step id and no user id', async () => {
    blockOn('awaiting_approval', approval('maintainer', { stepId: 'write-files' }));
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
    blockOn('awaiting_input', approval('maintainer', { approvalType: 'question', options: ['yes', 'no'] }));
    authService.currentUser.set(user('maintainer'));
    const answer = vi.spyOn(runService, 'answerQuestion').mockResolvedValue();
    vi.spyOn(runService, 'fetchApprovals').mockResolvedValue([]);

    expect(component.isAwaitingInput()).toBe(true);
    expect(component.approvalOptions()).toEqual(['yes', 'no']);

    component.selectOption('yes');
    await component.submitAnswer();

    expect(answer).toHaveBeenCalledWith('r1', 'ap1', 'yes');
  });

  it('treats non-list options as free text', () => {
    blockOn('awaiting_input', approval('maintainer', { options: { shape: 'object' } }));
    expect(component.approvalOptions()).toEqual([]);
  });

  it('has no pending approval once the run is running again', () => {
    blockOn('running', approval('maintainer', { status: 'approved' }));

    expect(component.isBlocked()).toBe(false);
    expect(component.pendingApproval()).toBeNull();
  });
});

/**
 * Temps réel : le `SignalRService` est remplacé par un faux hub à base de `Subject`, ce qui permet
 * de pousser des évènements à la main et d'observer le gabarit se réécrire sans rechargement.
 * Le `RunService` reste le vrai — seuls ses appels HTTP sont interceptés — pour que
 * `applyRunState` fasse son vrai travail d'insertion dans le signal `runs`.
 */
class SignalRServiceStub {
  readonly connectionState = signal<ConnectionState>('disconnected');
  readonly onRunEvent$ = new Subject<RunEvent>();
  readonly onRunState$ = new Subject<Run>();
  readonly onStepApproved$ = new Subject<unknown>();
  readonly onQuestionAnswered$ = new Subject<unknown>();
  readonly connect = vi.fn(async () => {
    this.connectionState.set('connected');
  });
  readonly disconnect = vi.fn(async () => {
    this.connectionState.set('disconnected');
  });
  readonly joinRun = vi.fn(async (_runId: string) => {});
  readonly leaveRun = vi.fn(async (_runId: string) => {});
}

class ArtifactServiceStub {
  readonly artifacts = signal<Artifact[]>([]);
  readonly isLoading = signal(false);
  readonly listArtifacts = vi.fn(async (_runId: string) => {});
  readonly download = vi.fn(async (_artifact: Artifact) => {});
}

describe('RunDetailComponent live updates', () => {
  let fixture: ComponentFixture<RunDetailComponent>;
  let component: RunDetailComponent;
  let hub: SignalRServiceStub;
  let runService: RunService;
  let authService: AuthService;
  let fetchApprovals: ReturnType<typeof vi.spyOn>;

  /** Approbations que le prochain `fetchApprovals` renverra — le hub déclenche ce rechargement. */
  let serverApprovals: Approval[];

  async function setup(role: UserRole | null = 'owner', initial = run('running')): Promise<void> {
    localStorage.clear();
    serverApprovals = [];
    hub = new SignalRServiceStub();
    TestBed.configureTestingModule({
      imports: [RunDetailComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: SignalRService, useValue: hub },
        { provide: ArtifactService, useClass: ArtifactServiceStub },
        { provide: ActivatedRoute, useValue: { params: of({ id: 'r1' }) } },
      ],
    });

    runService = TestBed.inject(RunService);
    authService = TestBed.inject(AuthService);
    authService.currentUser.set(role ? user(role) : null);

    // Le run initial vient du REST ; les rechargements suivants relisent ce que `runs` contient.
    runService.runs.set([initial]);
    vi.spyOn(runService, 'fetchRun').mockResolvedValue();
    vi.spyOn(runService, 'fetchEvents').mockResolvedValue([]);
    fetchApprovals = vi
      .spyOn(runService, 'fetchApprovals')
      .mockImplementation(async () => serverApprovals);

    fixture = TestBed.createComponent(RunDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await settle();
  }

  /** Laisse la file des microtâches se vider (connect/join/fetch) puis re-rend. */
  async function settle(): Promise<void> {
    for (let i = 0; i < 5; i++) await Promise.resolve();
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function all(testId: string): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`);
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('joining the hub', () => {
    it('connects and joins the run group for the route id', async () => {
      await setup();

      expect(hub.connect).toHaveBeenCalledTimes(1);
      expect(hub.joinRun).toHaveBeenCalledExactlyOnceWith('r1');
      expect(component.connectionState()).toBe('connected');
    });

    it('leaves the group and drops the connection on destroy', async () => {
      await setup();

      fixture.destroy();

      expect(hub.leaveRun).toHaveBeenCalledExactlyOnceWith('r1');
      expect(hub.disconnect).toHaveBeenCalledTimes(1);
    });

    it('stops appending events once destroyed', async () => {
      await setup();
      fixture.destroy();

      hub.onRunEvent$.next(runEvent(1));

      expect(component.events().length).toBe(0);
    });
  });

  describe('run events', () => {
    it('appends a pushed event to the open run and renders it', async () => {
      await setup();
      expect(el('no-events')).not.toBeNull();

      hub.onRunEvent$.next(runEvent(1, { eventType: 'log', message: 'compilation terminée' }));
      fixture.detectChanges();

      expect(all('run-event').length).toBe(1);
      expect(el('run-event')!.textContent).toContain('compilation terminée');
      expect(el('no-events')).toBeNull();
    });

    it('keeps pushed events in arrival order', async () => {
      await setup();

      hub.onRunEvent$.next(runEvent(1, { message: 'premier' }));
      hub.onRunEvent$.next(runEvent(2, { message: 'second' }));
      fixture.detectChanges();

      expect([...all('run-event')].map((e) => e.textContent!.trim())).toHaveLength(2);
      expect(component.events().map((e) => e.message)).toEqual(['premier', 'second']);
    });

    it('ignores an event addressed to another run', async () => {
      await setup();

      hub.onRunEvent$.next(runEvent(1, { runId: 'r-other', message: 'pas pour nous' }));
      fixture.detectChanges();

      expect(component.events()).toEqual([]);
      expect(el('no-events')).not.toBeNull();
    });

    it('caps the live buffer at 200 events, keeping the most recent', async () => {
      await setup();

      for (let seq = 1; seq <= 205; seq++) {
        hub.onRunEvent$.next(runEvent(seq));
      }

      expect(component.events().length).toBe(200);
      expect(component.events()[0].seq).toBe(6);
      expect(component.events().at(-1)!.seq).toBe(205);
    });

    it('does not refetch the run for a plain log event', async () => {
      await setup();
      (runService.fetchRun as unknown as ReturnType<typeof vi.fn>).mockClear();

      hub.onRunEvent$.next(runEvent(1, { eventType: 'log' }));

      expect(runService.fetchRun).not.toHaveBeenCalled();
    });

    it('refetches the run and its approvals on a state-changing event', async () => {
      await setup();
      (runService.fetchRun as unknown as ReturnType<typeof vi.fn>).mockClear();
      fetchApprovals.mockClear();

      hub.onRunEvent$.next(runEvent(1, { eventType: 'run.status_changed' }));
      await settle();

      expect(runService.fetchRun).toHaveBeenCalledExactlyOnceWith('r1');
      expect(fetchApprovals).toHaveBeenCalledExactlyOnceWith('r1');
    });
  });

  describe('run state', () => {
    it('updates the displayed status from a runState push, with no reload', async () => {
      await setup('owner', run('running'));
      expect(el('run-status')!.textContent).toContain('status.running');
      (runService.fetchRun as unknown as ReturnType<typeof vi.fn>).mockClear();

      hub.onRunState$.next(run('succeeded'));
      fixture.detectChanges();

      expect(el('run-status')!.textContent).toContain('status.succeeded');
      expect(el('run-status')!.className).toContain('green');
      expect(runService.fetchRun).not.toHaveBeenCalled();
    });

    it('hides the cancel button once the pushed state is terminal', async () => {
      await setup('owner', run('running'));
      expect(fixture.nativeElement.textContent).toContain('runDetail.cancel');

      hub.onRunState$.next(run('failed', { errorMessage: 'exit 1' }));
      fixture.detectChanges();

      expect(fixture.nativeElement.textContent).not.toContain('runDetail.cancel');
      expect(fixture.nativeElement.textContent).toContain('exit 1');
    });

    it('reacts to a decision made by another operator', async () => {
      await setup();
      (runService.fetchRun as unknown as ReturnType<typeof vi.fn>).mockClear();
      fetchApprovals.mockClear();

      hub.onStepApproved$.next({});
      hub.onQuestionAnswered$.next({});
      await settle();

      expect(runService.fetchRun).toHaveBeenCalledTimes(2);
      expect(fetchApprovals).toHaveBeenCalledTimes(2);
    });
  });

  describe('approval arriving live', () => {
    /** Rejoue ce que fait le serveur : le run se bloque et une approbation en attente apparaît. */
    async function raiseApproval(pending: Approval): Promise<void> {
      serverApprovals = [pending];
      hub.onRunState$.next(run('awaiting_approval'));
      hub.onRunEvent$.next(runEvent(1, { eventType: 'approval.requested' }));
      await settle();
    }

    it('shows the approval panel and its prompt without a reload', async () => {
      await setup('owner');
      expect(el('approval-panel')).toBeNull();

      await raiseApproval(approval('maintainer', { prompt: 'Publier en production ?' }));

      expect(el('approval-panel')).not.toBeNull();
      expect(el('approval-panel')!.textContent).toContain('Publier en production ?');
      expect(component.isAwaitingApproval()).toBe(true);
    });

    it('offers approve and reject to a user whose role satisfies the gate', async () => {
      await setup('owner');

      await raiseApproval(approval('maintainer'));

      expect(el('approve-run')).not.toBeNull();
      expect(el('reject-run')).not.toBeNull();
      expect(el('insufficient-role')).toBeNull();
    });

    it.each<UserRole>(['developer', 'viewer'])(
      'hides the decision buttons from a %s and says which role is needed',
      async (role) => {
        await setup(role);

        await raiseApproval(approval('maintainer'));

        expect(el('approve-run')).toBeNull();
        expect(el('reject-run')).toBeNull();
        expect(el('insufficient-role')).not.toBeNull();
      },
    );

    it('treats a signed-out user as unable to decide', async () => {
      await setup(null);

      await raiseApproval(approval('maintainer'));

      expect(component.canDecide()).toBe(false);
      expect(el('approve-run')).toBeNull();
    });

    it('falls back to the developer role when the gate pins none', async () => {
      await setup('developer');

      await raiseApproval(approval(null));

      expect(component.requiredRole()).toBe('developer');
      expect(el('approve-run')).not.toBeNull();
    });

    it('sends the decision for the approval that arrived over the wire', async () => {
      await setup('owner');
      await raiseApproval(approval('maintainer', { stepId: 'deploy' }));
      const approveRun = vi.spyOn(runService, 'approveRun').mockResolvedValue();

      el('approve-run')!.click();
      await settle();

      expect(approveRun).toHaveBeenCalledExactlyOnceWith('r1', {
        stepId: 'deploy',
        decision: 'approve',
        note: undefined,
      });
    });

    it('closes the panel when the hub says the gate was decided elsewhere', async () => {
      await setup('owner');
      await raiseApproval(approval('maintainer'));
      expect(el('approval-panel')).not.toBeNull();

      serverApprovals = [approval('maintainer', { status: 'approved', decidedBy: 'u2' })];
      hub.onRunState$.next(run('running'));
      hub.onStepApproved$.next({});
      await settle();

      expect(el('approval-panel')).toBeNull();
      expect(component.pendingApproval()).toBeNull();
    });

    it('shows the answer box instead of approve/reject for a question', async () => {
      await setup('owner');
      serverApprovals = [
        approval('maintainer', { approvalType: 'question', options: ['oui', 'non'] }),
      ];
      hub.onRunState$.next(run('awaiting_input'));
      hub.onRunEvent$.next(runEvent(1, { eventType: 'question.asked' }));
      await settle();

      expect(el('submit-answer')).not.toBeNull();
      expect(el('approve-run')).toBeNull();
      expect(component.approvalOptions()).toEqual(['oui', 'non']);
    });
  });

  /**
   * L'arbre de chaînage (lot 4).
   *
   * Le panneau ne s'affiche que si ce run fait partie d'une cascade : un arbre d'un seul nœud
   * n'apprendrait rien, et l'afficher quand même ajouterait une section vide à toutes les fiches.
   */
  describe('chain tree', () => {
    /** Une cascade racine → enfant → petit-enfant, dont le run affiché est le petit-enfant. */
    function cascade() {
      const grandchild = runTreeNode({
        id: 'r1',
        number: 3,
        chainDepth: 2,
        parentRunId: 'child',
        triggeredByType: 'chain',
        budgetUsedUsd: 1,
      });
      const child = runTreeNode({
        id: 'child',
        number: 2,
        chainDepth: 1,
        parentRunId: 'root',
        triggeredByType: 'chain',
        budgetUsedUsd: 2,
        children: [grandchild],
      });
      return runTree({ root: runTreeNode({ id: 'root', number: 1, budgetUsedUsd: 3, children: [child] }) });
    }

    it('stays hidden for a run that is not part of a chain', async () => {
      await setup('owner');
      runService.runTree.set(runTree());
      fixture.detectChanges();

      expect(el('run-chain')).toBeNull();
    });

    it('renders every node of the cascade, deepest included', async () => {
      await setup('owner');
      runService.runTree.set(cascade());
      fixture.detectChanges();

      expect(all('chain-node').length).toBe(3);
      // Aplati en profondeur d'abord : l'ordre de lecture est celui de l'arbre, pas celui de la
      // base.
      expect(all('chain-node')[0].textContent).toContain('#1');
      expect(all('chain-node')[2].textContent).toContain('#3');
      // Deux maillons sur trois sont chaînés ; la racine ne l'est pas.
      expect(all('chain-badge').length).toBe(2);
    });

    it('reports the totals of the whole cascade, not of the run being viewed', async () => {
      await setup('owner');
      runService.runTree.set(cascade());
      fixture.detectChanges();

      // Devant une cascade, la question est ce que L'ENSEMBLE a coûté.
      expect(component.runTree()!.totalRuns).toBe(3);
      expect(component.runTree()!.totalBudgetUsedUsd).toBe(6);
      expect(component.runTree()!.maxDepth).toBe(2);
      expect(el('chain-summary')).not.toBeNull();
    });

    it('indents each node by its depth', async () => {
      await setup('owner');
      runService.runTree.set(cascade());
      fixture.detectChanges();

      expect(component.treeRows().map((r) => r.depth)).toEqual([0, 1, 2]);
    });
  });
});
