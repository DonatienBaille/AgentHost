import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { computed, signal } from '@angular/core';
import { RunsListComponent } from './runs-list.component';
import { RunService } from '../../services/run.service';
import { Run } from '../../core/models';
import { run } from '../../core/testing/fixtures';

/** Double du service : mêmes signaux et mêmes dérivations que le vrai, aucun HTTP. */
class RunServiceStub {
  readonly runs = signal<Run[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly failedRuns = computed(
    () =>
      this.runs().filter((r) =>
        ['failed', 'rejected', 'timed_out', 'budget_exceeded', 'infra_error'].includes(r.status),
      ).length,
  );
  readonly successRate = computed(() => {
    const total = this.runs().length;
    return total > 0 ? (this.runs().filter((r) => r.status === 'succeeded').length / total) * 100 : 0;
  });
  readonly listRuns = vi.fn(async (_skip?: number, _take?: number) => {});
  readonly selectRun = vi.fn((_id: string) => {});
}

describe('RunsListComponent', () => {
  let fixture: ComponentFixture<RunsListComponent>;
  let component: RunsListComponent;
  let runService: RunServiceStub;

  function setup(): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [RunsListComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: RunService, useClass: RunServiceStub },
      ],
    });

    runService = TestBed.inject(RunService) as unknown as RunServiceStub;
    // Les cartes sont des `routerLink` : sans ce court-circuit, un clic lance une vraie navigation
    // dont la promesse se résout après la destruction du TestBed (NG0205).
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture = TestBed.createComponent(RunsListComponent);
    component = fixture.componentInstance;
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

  describe('loading', () => {
    it('loads the default page on init, without paging arguments', () => {
      setup();

      expect(runService.listRuns).toHaveBeenCalledTimes(1);
      expect(runService.listRuns).toHaveBeenCalledWith();
    });

    it('hides the whole list section while loading, spinner only', () => {
      setup();
      runService.runs.set([run('succeeded')]);
      runService.isLoading.set(true);
      fixture.detectChanges();

      expect(el('runs-spinner')).not.toBeNull();
      expect(all('run-card').length).toBe(0);
      expect(el('runs-empty')).toBeNull();
    });

    it('reloads through the refresh button', () => {
      setup();
      runService.listRuns.mockClear();

      el('runs-refresh')!.click();

      expect(runService.listRuns).toHaveBeenCalledTimes(1);
    });
  });

  describe('empty state', () => {
    it('shows the empty block and no card', () => {
      setup();

      expect(all('run-card').length).toBe(0);
      expect(el('runs-empty')).not.toBeNull();
    });

    it('shows a 0% success rate rather than NaN in the header', () => {
      setup();

      expect(fixture.nativeElement.textContent).toContain('runs.successRate: 0%');
    });
  });

  describe('error state', () => {
    it('shows the service error and suppresses the empty block', () => {
      setup();
      runService.error.set('Failed to load runs');
      fixture.detectChanges();

      expect(el('runs-error')!.textContent).toContain('Failed to load runs');
      // L'état vide est réservé au « rien à afficher » légitime, pas à l'échec de chargement.
      expect(el('runs-empty')).toBeNull();
    });

    it('still renders the cards it holds when an error arrives', () => {
      setup();
      runService.runs.set([run('succeeded', { id: 'r1' })]);
      runService.error.set('Failed to load runs');
      fixture.detectChanges();

      expect(el('runs-error')).not.toBeNull();
      expect(all('run-card').length).toBe(1);
    });
  });

  describe('loaded state', () => {
    it('renders one card per run with number, agent and status label', () => {
      setup();
      runService.runs.set([
        run('succeeded', { id: 'r1', number: 7, agentId: 'ag-writer' }),
        run('running', { id: 'r2', number: 8, agentId: 'ag-linter' }),
      ]);
      fixture.detectChanges();

      const cards = all('run-card');
      expect(cards.length).toBe(2);
      expect(cards[0].textContent).toContain('#7');
      expect(cards[0].textContent).toContain('ag-writer');
      // Clés non résolues en test : ngx-translate rend la clé telle quelle.
      expect(cards[0].textContent).toContain('status.succeeded');
      expect(cards[1].textContent).toContain('status.running');
    });

    it('links each card to the run detail route', () => {
      setup();
      runService.runs.set([run('succeeded', { id: 'r-xyz' })]);
      fixture.detectChanges();

      expect(all('run-card')[0].getAttribute('href')).toBe('/runs/r-xyz');
    });

    it('shows the duration in whole seconds only when the run has one', () => {
      setup();
      runService.runs.set([
        run('succeeded', { id: 'r1', durationMs: 4500 }),
        run('running', { id: 'r2', durationMs: null }),
      ]);
      fixture.detectChanges();

      const durations = all('run-duration');
      expect(durations.length).toBe(1);
      expect(durations[0].textContent).toContain('5s');
    });

    it('hides the duration for a zero-millisecond run — pinned current behaviour', () => {
      // DÉFAUT ÉPINGLÉ (runs-list.component.html:55) : `@if (run.durationMs)` teste la véracité,
      // donc une durée mesurée à 0 ms est traitée comme « pas de durée » et la ligne disparaît.
      setup();
      runService.runs.set([run('succeeded', { id: 'r1', durationMs: 0 })]);
      fixture.detectChanges();

      expect(all('run-duration').length).toBe(0);
    });

    it('colours the badge from the shared status mapping', () => {
      setup();
      runService.runs.set([
        run('succeeded', { id: 'r1' }),
        run('budget_exceeded', { id: 'r2' }),
        run('awaiting_approval', { id: 'r3' }),
        run('queued', { id: 'r4' }),
      ]);
      fixture.detectChanges();

      const badges = [...all('run-card')].map(
        (c) => c.querySelector('[class*="uppercase"]')!.className,
      );
      expect(badges[0]).toContain('green');
      expect(badges[1]).toContain('red');
      expect(badges[2]).toContain('yellow');
      expect(badges[3]).toContain('gray');
    });

    it('summarises total, success rate and failures in the header', () => {
      setup();
      runService.runs.set([
        run('succeeded', { id: 'r1' }),
        run('failed', { id: 'r2' }),
        run('infra_error', { id: 'r3' }),
        run('running', { id: 'r4' }),
      ]);
      fixture.detectChanges();

      const header = fixture.nativeElement.textContent as string;
      expect(header).toContain('runs.total: 4');
      expect(header).toContain('runs.successRate: 25%');
      expect(header).toContain('runs.failed: 2');
    });
  });

  describe('selection', () => {
    it('selects the run by id when its card is clicked', () => {
      setup();
      runService.runs.set([run('succeeded', { id: 'r1' }), run('failed', { id: 'r2' })]);
      fixture.detectChanges();

      all('run-card')[1].click();

      expect(runService.selectRun).toHaveBeenCalledTimes(1);
      expect(runService.selectRun).toHaveBeenCalledWith('r2');
    });

    it('delegates selectRun straight through without extra arguments', () => {
      setup();

      component.selectRun('r-direct');

      expect(runService.selectRun).toHaveBeenCalledWith('r-direct');
    });
  });

  describe('role gating', () => {
    it('offers refresh as its only action: the list is read-only for every role', () => {
      setup();
      runService.runs.set([run('running', { id: 'r1' })]);
      fixture.detectChanges();

      const buttons = fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLElement>;
      expect(buttons.length).toBe(1);
      expect(buttons[0].getAttribute('data-testid')).toBe('runs-refresh');
      expect(fixture.nativeElement.querySelectorAll('form').length).toBe(0);
    });
  });
});
