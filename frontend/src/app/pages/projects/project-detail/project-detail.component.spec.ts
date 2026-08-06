import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { Subject, of } from 'rxjs';
import { ProjectDetailComponent } from './project-detail.component';
import { ProjectService } from '../../../services/project.service';
import { SignalRService } from '../../../services/signalr.service';
import { MemoryService } from '../../../services/memory.service';
import { Project, ProjectMemory, Run } from '../../../core/models';
import { project, run } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class ProjectServiceStub {
  readonly projects = signal<Project[]>([]);
  readonly currentProjectId = signal<string | null>(null);
  readonly currentProject = signal<Project | null>(null);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly selectProject = vi.fn((id: string) => this.currentProjectId.set(id));
  readonly fetchProject = vi.fn(async (_id: string) => {});
}

/** Double du hub : `onRecentRuns$` est piloté à la main par les tests. */
class SignalRServiceStub {
  readonly recentRuns$ = new Subject<Run[]>();
  readonly onRecentRuns$ = this.recentRuns$.asObservable();
  readonly joinProject = vi.fn(async (_projectId: string) => {});
  readonly leaveProject = vi.fn(async (_projectId: string) => {});
  readonly disconnectProject = vi.fn(async () => {});
}

/** Double minimal : le panneau mémoire est couvert par sa propre spec. */
class MemoryServiceStub {
  readonly memory = signal<ProjectMemory | null>(null);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly load = vi.fn(async (_projectId: string) => {});
  readonly dispose = vi.fn(() => {});
}

describe('ProjectDetailComponent', () => {
  let fixture: ComponentFixture<ProjectDetailComponent>;
  let component: ProjectDetailComponent;
  let projectService: ProjectServiceStub;
  let signalR: SignalRServiceStub;
  let memoryService: MemoryServiceStub;

  function setup(params: Record<string, string> = { id: 'p1' }): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [ProjectDetailComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: ProjectService, useClass: ProjectServiceStub },
        { provide: SignalRService, useClass: SignalRServiceStub },
        { provide: MemoryService, useClass: MemoryServiceStub },
        { provide: ActivatedRoute, useValue: { params: of(params) } },
      ],
    });

    projectService = TestBed.inject(ProjectService) as unknown as ProjectServiceStub;
    signalR = TestBed.inject(SignalRService) as unknown as SignalRServiceStub;
    memoryService = TestBed.inject(MemoryService) as unknown as MemoryServiceStub;

    fixture = TestBed.createComponent(ProjectDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  /** Fait apparaître le projet courant, comme le ferait `fetchProject`. */
  function loadProject(overrides: Partial<Project> = {}): Project {
    const p = project({ id: 'p1', ...overrides });
    projectService.currentProject.set(p);
    fixture.detectChanges();
    return p;
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

  describe('route wiring', () => {
    it('selects, fetches and joins the project of the route', () => {
      setup({ id: 'p42' });

      expect(projectService.selectProject).toHaveBeenCalledWith('p42');
      expect(projectService.fetchProject).toHaveBeenCalledWith('p42');
      expect(signalR.joinProject).toHaveBeenCalledWith('p42');
    });

    it('does nothing at all when the route carries no id', () => {
      setup({});

      expect(projectService.selectProject).not.toHaveBeenCalled();
      expect(projectService.fetchProject).not.toHaveBeenCalled();
      expect(signalR.joinProject).not.toHaveBeenCalled();
      expect(component.projectId()).toBe('');
    });

    it('falls back to the route id before the project has loaded', () => {
      setup({ id: 'p42' });

      expect(projectService.currentProject()).toBeNull();
      expect(component.projectId()).toBe('p42');
    });

    it('prefers the loaded project id once it arrives', () => {
      setup({ id: 'p42' });
      projectService.currentProject.set(project({ id: 'p1' }));

      expect(component.projectId()).toBe('p1');
    });

    it('leaves the project group and disconnects on destroy', () => {
      setup({ id: 'p42' });

      fixture.destroy();

      expect(signalR.leaveProject).toHaveBeenCalledWith('p42');
      expect(signalR.disconnectProject).toHaveBeenCalledTimes(1);
    });

    it('does not try to leave a group it never joined', () => {
      setup({});

      fixture.destroy();

      expect(signalR.leaveProject).not.toHaveBeenCalled();
    });

    it('stops applying hub payloads after destroy', () => {
      setup();
      fixture.destroy();

      signalR.recentRuns$.next([run('succeeded')]);

      expect(component.recentRuns()).toEqual([]);
    });
  });

  describe('states', () => {
    it('renders nothing but the banner while the project is unknown', () => {
      setup();

      expect(el('project-name')).toBeNull();
      expect(el('link-new-run')).toBeNull();
      expect(el('runs-empty')).toBeNull();
    });

    it('renders the error signal of the service', () => {
      setup();
      projectService.error.set('errors.loadProject');
      fixture.detectChanges();

      const banner = el('project-error');
      expect(banner).not.toBeNull();
      expect(banner!.textContent).toContain('errors.loadProject');
    });

    it('shows the spinner while loading', () => {
      setup();
      projectService.isLoading.set(true);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.animate-spin')).not.toBeNull();
    });

    it('shows the spinner and the error side by side while reloading after a failure', () => {
      setup();
      // Comportement ACTUEL épinglé : les deux blocs sont indépendants, ils peuvent coexister.
      projectService.isLoading.set(true);
      projectService.error.set('errors.loadProject');
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('.animate-spin')).not.toBeNull();
      expect(el('project-error')).not.toBeNull();
    });

    it('renders the header and the action links of the loaded project', () => {
      setup();
      loadProject({ name: 'Site vitrine', description: 'Refonte du portail' });

      expect(el('project-name')!.textContent!.trim()).toBe('Site vitrine');
      expect(el('project-description')!.textContent!.trim()).toBe('Refonte du portail');
      expect(el('link-agents')!.getAttribute('href')).toBe('/projects/p1/agents');
      expect(el('link-webhooks')!.getAttribute('href')).toBe('/admin/webhooks/p1');
      expect(el('link-new-run')!.getAttribute('href')).toBe('/projects/p1/new-run');
    });

    it('omits the description paragraph when the project has none', () => {
      setup();
      loadProject({ description: null });

      expect(el('project-name')).not.toBeNull();
      expect(el('project-description')).toBeNull();
    });

    it('builds the links from the project id, not from the route id', () => {
      setup({ id: 'stale' });
      loadProject({ id: 'p9' });

      expect(el('link-new-run')!.getAttribute('href')).toBe('/projects/p9/new-run');
    });
  });

  describe('recent runs from the hub', () => {
    it('shows the empty state and no row before the hub emits', () => {
      setup();
      loadProject();

      expect(all('run-row').length).toBe(0);
      expect(el('runs-empty')).not.toBeNull();
    });

    it('never queries the REST run list: the panel is hub-fed only', () => {
      setup();
      loadProject();

      // Comportement ACTUEL épinglé : sans émission du hub, le panneau reste vide même si
      // le projet a des runs côté serveur (cf. rapport).
      expect(component.recentRuns()).toEqual([]);
    });

    it('renders one row per run pushed by the hub, with its link', () => {
      setup();
      loadProject();

      signalR.recentRuns$.next([
        run('succeeded', { id: 'r1', number: 7, agentId: 'ag-writer' }),
        run('failed', { id: 'r2', number: 6, agentId: 'ag-linter' }),
      ]);
      fixture.detectChanges();

      const rows = all('run-row');
      expect(rows.length).toBe(2);
      expect(el('runs-empty')).toBeNull();
      expect(rows[0].textContent).toContain('#7');
      expect(rows[0].textContent).toContain('ag-writer');
      // Clés non résolues en test : ngx-translate rend la clé telle quelle.
      expect(rows[0].textContent).toContain('status.succeeded');
      expect(rows[0].getAttribute('href')).toBe('/runs/r1');
      expect(rows[1].getAttribute('href')).toBe('/runs/r2');
    });

    it('keeps the hub order as-is, without re-sorting', () => {
      setup();
      loadProject();

      signalR.recentRuns$.next([
        run('succeeded', { id: 'old', createdAt: '2026-01-01T00:00:00Z' }),
        run('succeeded', { id: 'new', createdAt: '2026-03-01T00:00:00Z' }),
      ]);

      expect(component.recentRuns().map((r) => r.id)).toEqual(['old', 'new']);
    });

    it('replaces the whole list on each emission', () => {
      setup();
      loadProject();

      signalR.recentRuns$.next([run('succeeded', { id: 'r1' }), run('failed', { id: 'r2' })]);
      signalR.recentRuns$.next([run('running', { id: 'r3' })]);
      fixture.detectChanges();

      const rows = all('run-row');
      expect(rows.length).toBe(1);
      expect(rows[0].getAttribute('href')).toBe('/runs/r3');
    });

    it('falls back to the empty state when the hub pushes an empty list', () => {
      setup();
      loadProject();
      signalR.recentRuns$.next([run('succeeded')]);
      fixture.detectChanges();

      signalR.recentRuns$.next([]);
      fixture.detectChanges();

      expect(all('run-row').length).toBe(0);
      expect(el('runs-empty')).not.toBeNull();
    });

    it('keeps the runs hidden while the project itself is unknown', () => {
      setup();
      signalR.recentRuns$.next([run('succeeded')]);
      fixture.detectChanges();

      // Le panneau vit à l'intérieur du bloc `@if (project())`.
      expect(all('run-row').length).toBe(0);
    });
  });

  describe('memory panel', () => {
    it('hands the project id down to the memory panel', () => {
      setup({ id: 'p42' });
      loadProject({ id: 'p42' });

      expect(memoryService.load).toHaveBeenCalledWith('p42');
    });

    it('does not load any memory before an id is known', () => {
      setup({});

      expect(memoryService.load).not.toHaveBeenCalled();
    });
  });
});
