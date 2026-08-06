import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { DashboardComponent } from './dashboard.component';
import { RunService } from '../../services/run.service';
import { ProjectService } from '../../services/project.service';
import { RunStatus } from '../../core/models';
import { project, run } from '../../core/testing/fixtures';

/**
 * On garde ici les vrais `RunService`/`ProjectService` : les agrégats du tableau de bord
 * (`runCount`, `activeRuns`, `failedRuns`, `successRate`) sont des `computed` du service, et
 * c'est précisément ce couplage que la page doit respecter. Seuls les appels HTTP sont neutralisés.
 */
describe('DashboardComponent', () => {
  let fixture: ComponentFixture<DashboardComponent>;
  let component: DashboardComponent;
  let runService: RunService;
  let projectService: ProjectService;

  function setup(): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [DashboardComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
      ],
    });

    runService = TestBed.inject(RunService);
    projectService = TestBed.inject(ProjectService);
    vi.spyOn(runService, 'listRuns').mockResolvedValue();
    vi.spyOn(projectService, 'listProjects').mockResolvedValue();

    fixture = TestBed.createComponent(DashboardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function all(testId: string): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`);
  }

  function text(testId: string): string {
    return el(testId)!.textContent!.trim();
  }

  /** Remplit le service avec un run par statut demandé, chacun avec un id distinct. */
  function loadRuns(...statuses: RunStatus[]): void {
    runService.runs.set(statuses.map((status, i) => run(status, { id: `r${i}`, number: i + 1 })));
    fixture.detectChanges();
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('initial load', () => {
    it('asks for the first page of runs and for the projects', () => {
      setup();

      expect(runService.listRuns).toHaveBeenCalledWith(0, 20);
      expect(projectService.listProjects).toHaveBeenCalledTimes(1);
    });
  });

  describe('empty states', () => {
    it('shows both empty states and no card at all', () => {
      setup();

      expect(all('run-card').length).toBe(0);
      expect(all('project-card').length).toBe(0);
      expect(el('runs-empty')).not.toBeNull();
      expect(el('projects-empty')).not.toBeNull();
    });

    it('hides the runs empty state while loading, so it never flashes', () => {
      setup();
      runService.isLoading.set(true);
      fixture.detectChanges();

      expect(el('runs-empty')).toBeNull();
      expect(fixture.nativeElement.querySelector('.animate-spin')).not.toBeNull();
    });

    it('shows zeroed tiles rather than blanks when there is no run', () => {
      setup();

      expect(text('stat-total-runs')).toBe('0');
      expect(text('stat-active-runs')).toBe('0');
      expect(text('stat-failed-runs')).toBe('0');
      // Aucune division par zéro : le service retourne 0 quand il n'y a aucun run.
      expect(text('stat-success-rate')).toBe('0%');
      expect(Number.isNaN(component.successRate())).toBe(false);
    });
  });

  describe('error state', () => {
    it('renders the run service error signal', () => {
      setup();
      runService.error.set('Failed to load runs');
      fixture.detectChanges();

      const banner = el('dashboard-error');
      expect(banner).not.toBeNull();
      expect(banner!.textContent).toContain('Failed to load runs');
    });

    it('keeps rendering the tiles alongside the error banner', () => {
      setup();
      loadRuns('succeeded');
      runService.error.set('Failed to load runs');
      fixture.detectChanges();

      expect(el('dashboard-error')).not.toBeNull();
      expect(text('stat-total-runs')).toBe('1');
    });

    it('shows no banner when the project service alone fails: the page only reads run errors', () => {
      setup();
      // Comportement ACTUEL épinglé : le tableau de bord n'affiche que `runService.error`,
      // une panne de la liste des projets reste donc silencieuse (cf. rapport).
      projectService.error.set('Failed to load projects');
      fixture.detectChanges();

      expect(el('dashboard-error')).toBeNull();
    });
  });

  describe('aggregates', () => {
    it('counts every run, whatever its status', () => {
      setup();
      loadRuns('succeeded', 'failed', 'running', 'cancelled');

      expect(component.runCount()).toBe(4);
      expect(text('stat-total-runs')).toBe('4');
    });

    it('counts the eight in-flight statuses as active', () => {
      setup();
      loadRuns(
        'pending',
        'queued',
        'provisioning',
        'preparing',
        'running',
        'awaiting_approval',
        'awaiting_input',
        'finalizing',
      );

      expect(component.activeRuns()).toBe(8);
      expect(text('stat-active-runs')).toBe('8');
    });

    it('counts the five terminal failure statuses as failed', () => {
      setup();
      loadRuns('failed', 'rejected', 'timed_out', 'budget_exceeded', 'infra_error');

      expect(component.failedRuns()).toBe(5);
      expect(text('stat-failed-runs')).toBe('5');
    });

    it('excludes cancelled runs from both the active and the failed tiles', () => {
      setup();
      loadRuns('cancelled', 'cancelled');

      expect(component.runCount()).toBe(2);
      expect(component.activeRuns()).toBe(0);
      expect(component.failedRuns()).toBe(0);
    });

    it('computes the success rate over every run, not only the finished ones', () => {
      setup();
      // 2 réussis sur 5 runs -> 40 %, et non 2/3 des runs terminés.
      loadRuns('succeeded', 'succeeded', 'failed', 'running', 'cancelled');

      expect(component.successRate()).toBe(40);
      expect(text('stat-success-rate')).toBe('40%');
    });

    it('rounds the displayed success rate to a whole percent', () => {
      setup();
      // 1/3 = 33.33… : la valeur brute reste précise, seul l'affichage est arrondi.
      loadRuns('succeeded', 'failed', 'failed');

      expect(component.successRate()).toBeCloseTo(33.333, 3);
      expect(text('stat-success-rate')).toBe('33%');
    });

    it('reaches 100% when every run succeeded', () => {
      setup();
      loadRuns('succeeded', 'succeeded');

      expect(component.successRate()).toBe(100);
      expect(text('stat-success-rate')).toBe('100%');
    });

    it('recomputes the tiles when the run list changes', () => {
      setup();
      loadRuns('running');
      expect(text('stat-active-runs')).toBe('1');

      loadRuns('succeeded', 'succeeded');
      expect(text('stat-active-runs')).toBe('0');
      expect(text('stat-total-runs')).toBe('2');
      expect(text('stat-success-rate')).toBe('100%');
    });
  });

  describe('recent runs list', () => {
    it('renders one card per run with its number and agent, linked to the run', () => {
      setup();
      runService.runs.set([
        run('succeeded', {
          id: 'r1',
          number: 12,
          agentId: 'ag-writer',
          createdAt: '2026-02-01T00:00:00Z',
        }),
        run('failed', {
          id: 'r2',
          number: 11,
          agentId: 'ag-linter',
          createdAt: '2026-01-01T00:00:00Z',
        }),
      ]);
      fixture.detectChanges();

      const cards = all('run-card');
      expect(cards.length).toBe(2);
      expect(el('runs-empty')).toBeNull();
      expect(cards[0].textContent).toContain('#12');
      expect(cards[0].textContent).toContain('ag-writer');
      expect(cards[0].getAttribute('href')).toBe('/runs/r1');
      expect(cards[1].getAttribute('href')).toBe('/runs/r2');
    });

    it('keeps every run when two share the same timestamp', () => {
      setup();
      runService.runs.set([
        run('succeeded', { id: 'a', createdAt: '2026-01-01T00:00:00Z' }),
        run('succeeded', { id: 'b', createdAt: '2026-01-01T00:00:00Z' }),
      ]);
      fixture.detectChanges();

      // Comportement ACTUEL épinglé : le comparateur de `recentRuns()` ne renvoie jamais 0
      // (`a.createdAt < b.createdAt ? 1 : -1`), l'ordre de deux runs à égalité de date n'est donc
      // pas défini. On vérifie seulement qu'aucun run n'est perdu ni dupliqué (cf. rapport).
      expect(component.recentRuns().map((r) => r.id).sort()).toEqual(['a', 'b']);
      expect(all('run-card').length).toBe(2);
    });

    it('shows the translated status badge of each run', () => {
      setup();
      loadRuns('awaiting_approval');

      // Clés non résolues en test : ngx-translate rend la clé telle quelle.
      expect(all('run-card')[0].textContent).toContain('status.awaiting_approval');
    });

    it('sorts the most recent run first', () => {
      setup();
      runService.runs.set([
        run('succeeded', { id: 'old', number: 1, createdAt: '2026-01-01T00:00:00Z' }),
        run('succeeded', { id: 'new', number: 3, createdAt: '2026-03-01T00:00:00Z' }),
        run('succeeded', { id: 'mid', number: 2, createdAt: '2026-02-01T00:00:00Z' }),
      ]);
      fixture.detectChanges();

      expect(component.recentRuns().map((r) => r.id)).toEqual(['new', 'mid', 'old']);
      expect(all('run-card')[0].getAttribute('href')).toBe('/runs/new');
    });

    it('caps the list at eight cards even when the service holds more', () => {
      setup();
      runService.runs.set(
        Array.from({ length: 20 }, (_, i) =>
          run('succeeded', {
            id: `r${i}`,
            number: i,
            createdAt: `2026-01-${String(i + 1).padStart(2, '0')}T00:00:00Z`,
          }),
        ),
      );
      fixture.detectChanges();

      expect(all('run-card').length).toBe(8);
      // Les huit plus récents, pas les huit premiers reçus.
      expect(component.recentRuns()[0].id).toBe('r19');
      // Les tuiles, elles, comptent bien les 20 runs.
      expect(text('stat-total-runs')).toBe('20');
    });

    it('does not mutate the service list while sorting', () => {
      setup();
      runService.runs.set([
        run('succeeded', { id: 'old', createdAt: '2026-01-01T00:00:00Z' }),
        run('succeeded', { id: 'new', createdAt: '2026-03-01T00:00:00Z' }),
      ]);

      component.recentRuns();

      expect(runService.runs().map((r) => r.id)).toEqual(['old', 'new']);
    });
  });

  describe('projects panel', () => {
    it('renders one card per project, linked to the project detail', () => {
      setup();
      projectService.projects.set([
        project({ id: 'p1', name: 'Site vitrine' }),
        project({ id: 'p2', name: 'API interne' }),
      ]);
      fixture.detectChanges();

      const cards = all('project-card');
      expect(cards.length).toBe(2);
      expect(el('projects-empty')).toBeNull();
      expect(cards[0].textContent).toContain('Site vitrine');
      expect(cards[0].getAttribute('href')).toBe('/projects/p1');
      expect(cards[1].getAttribute('href')).toBe('/projects/p2');
    });

    it('shows the description only when the project has one', () => {
      setup();
      projectService.projects.set([
        project({ id: 'p1', description: null }),
        project({ id: 'p2', description: 'Refonte du portail' }),
      ]);
      fixture.detectChanges();

      const cards = all('project-card');
      expect(cards[0].querySelectorAll('div').length).toBe(1);
      expect(cards[1].textContent).toContain('Refonte du portail');
    });

    it('keeps showing the projects when the runs failed to load', () => {
      setup();
      runService.error.set('Failed to load runs');
      projectService.projects.set([project()]);
      fixture.detectChanges();

      expect(all('project-card').length).toBe(1);
    });
  });
});
