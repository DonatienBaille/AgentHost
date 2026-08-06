import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { Subject } from 'rxjs';
import { ProjectMemoryComponent } from './project-memory.component';
import { MemoryService } from '../../services/memory.service';
import { ProjectService } from '../../services/project.service';
import { SignalRService } from '../../services/signalr.service';
import { MemoryUpdate, ProjectMemory } from '../../core/models';
import { projectMemory } from '../../core/testing/fixtures';

/**
 * Ce spec branche le VRAI `MemoryService` sur un faux hub : la chaîne
 * hub -> service -> signal -> gabarit est donc exercée de bout en bout, ce qui est le seul moyen
 * de vérifier que le temps réel rafraîchit réellement l'affichage.
 */
class SignalRServiceStub {
  readonly onMemoryLoaded$ = new Subject<ProjectMemory>();
  readonly onMemoryUpdated$ = new Subject<MemoryUpdate>();
  readonly joinProjectMemory = vi.fn(async (_projectId: string) => {});
  readonly leaveProjectMemory = vi.fn(async (_projectId: string) => {});
  readonly updateMemory = vi.fn(async (_projectId: string, _update: MemoryUpdate) => {});
}

class ProjectServiceStub {
  readonly fetchMemory = vi.fn(async (id: string) => projectMemory({ projectId: id }));
}

describe('ProjectMemoryComponent', () => {
  let fixture: ComponentFixture<ProjectMemoryComponent>;
  let signalR: SignalRServiceStub;
  let projects: ProjectServiceStub;

  function configure(): void {
    localStorage.clear();
    signalR = new SignalRServiceStub();
    projects = new ProjectServiceStub();
    TestBed.configureTestingModule({
      imports: [ProjectMemoryComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: SignalRService, useValue: signalR },
        { provide: ProjectService, useValue: projects },
      ],
    });
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
  }

  /** Monte le composant et laisse le chargement REST initial se terminer. */
  async function setup(projectId = 'p1'): Promise<void> {
    configure();
    fixture = TestBed.createComponent(ProjectMemoryComponent);
    fixture.componentRef.setInput('projectId', projectId);
    fixture.detectChanges();
    await settle();
  }

  /** Vide la file des microtâches (chargement REST, refetch) puis re-rend. */
  async function settle(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
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

  describe('initial load', () => {
    it('fetches the memory of the project passed as input and joins its hub group', async () => {
      await setup('p-42');

      expect(projects.fetchMemory).toHaveBeenCalledWith('p-42');
      expect(signalR.joinProjectMemory).toHaveBeenCalledWith('p-42');
    });

    it('reloads and re-joins when the projectId input changes', async () => {
      await setup('p1');

      fixture.componentRef.setInput('projectId', 'p2');
      fixture.detectChanges();
      await settle();

      expect(projects.fetchMemory).toHaveBeenLastCalledWith('p2');
      expect(signalR.leaveProjectMemory).toHaveBeenCalledWith('p1');
      expect(signalR.joinProjectMemory).toHaveBeenLastCalledWith('p2');
    });
  });

  describe('empty state', () => {
    it('shows every "nothing here" line when the memory has no entries', async () => {
      await setup();

      expect(el('no-patterns')).not.toBeNull();
      expect(el('no-learnings')).not.toBeNull();
      expect(el('no-notes')).not.toBeNull();
      expect(el('no-run-history')).not.toBeNull();
      expect(all('pattern-row').length).toBe(0);
      expect(all('note-row').length).toBe(0);
    });

    it('renders nothing at all before the first snapshot arrives', async () => {
      configure();
      let resolveFetch!: (m: ProjectMemory) => void;
      projects.fetchMemory.mockReturnValueOnce(
        new Promise<ProjectMemory>((resolve) => {
          resolveFetch = resolve;
        }),
      );
      fixture = TestBed.createComponent(ProjectMemoryComponent);
      fixture.componentRef.setInput('projectId', 'p1');
      fixture.detectChanges();

      expect(el('memory-spinner')).not.toBeNull();
      expect(el('memory-context')).toBeNull();

      resolveFetch(projectMemory());
      await settle();
      expect(el('memory-context')).not.toBeNull();
    });
  });

  describe('error state', () => {
    it('shows the load error and no memory card', async () => {
      configure();
      projects.fetchMemory.mockRejectedValueOnce(new Error('boom'));
      fixture = TestBed.createComponent(ProjectMemoryComponent);
      fixture.componentRef.setInput('projectId', 'p1');
      fixture.detectChanges();
      await settle();

      expect(el('memory-error')!.textContent).toContain('errors.loadMemory');
      expect(el('memory-context')).toBeNull();
      // L'échec REST n'empêche pas de rejoindre le groupe : les pushs suivants peuvent réparer.
      expect(signalR.joinProjectMemory).toHaveBeenCalledWith('p1');
    });
  });

  describe('loaded state', () => {
    it('renders the context, its technologies and its recent decisions', async () => {
      configure();
      projects.fetchMemory.mockResolvedValueOnce(
        projectMemory({
          context: {
            name: 'Site vitrine',
            description: 'Vitrine publique',
            technologies: ['Angular', 'Tailwind'],
            recentDecisions: ['Passer en signals'],
          },
        }),
      );
      fixture = TestBed.createComponent(ProjectMemoryComponent);
      fixture.componentRef.setInput('projectId', 'p1');
      fixture.detectChanges();
      await settle();

      expect(el('memory-context')!.textContent).toContain('Site vitrine');
      expect(el('memory-context')!.textContent).toContain('Vitrine publique');
      expect([...all('memory-tech')].map((t) => t.textContent!.trim())).toEqual([
        'Angular',
        'Tailwind',
      ]);
      expect(all('memory-decision')[0].textContent).toContain('Passer en signals');
    });

    it('renders one row per pattern, learning, note and history item', async () => {
      configure();
      projects.fetchMemory.mockResolvedValueOnce(
        projectMemory({
          patterns: [
            { name: 'retry', frequency: 3, lastSeen: '2026-02-01T00:00:00Z' },
            { name: 'timeout', frequency: 1, lastSeen: '2026-02-02T00:00:00Z' },
          ],
          learnings: [
            {
              id: 'l1',
              title: 'Toujours borner le budget',
              description: 'Sinon la boucle coûte cher',
              createdAt: '2026-02-01T00:00:00Z',
              appliedByAgents: ['redacteur'],
            },
          ],
          notes: [
            { id: 'n1', agentName: 'redacteur', text: 'Brief validé', timestamp: '2026-02-01T00:00:00Z' },
          ],
          runHistory: [
            {
              runId: 'r1',
              agentName: 'redacteur',
              timestamp: '2026-02-01T00:00:00Z',
              outcome: 'succeeded',
              summary: 'Article publié',
            },
          ],
        }),
      );
      fixture = TestBed.createComponent(ProjectMemoryComponent);
      fixture.componentRef.setInput('projectId', 'p1');
      fixture.detectChanges();
      await settle();

      expect(all('pattern-row').length).toBe(2);
      expect(all('pattern-row')[0].textContent).toContain('retry');
      expect(all('pattern-row')[0].textContent).toContain('×3');
      expect(all('learning-row')[0].textContent).toContain('Toujours borner le budget');
      expect(all('learning-row')[0].textContent).toContain('memory.appliedBy: redacteur');
      expect(all('note-row')[0].textContent).toContain('Brief validé');
      expect(el('no-patterns')).toBeNull();
    });

    it('links each history item to its run and colours the outcome badge', async () => {
      configure();
      projects.fetchMemory.mockResolvedValueOnce(
        projectMemory({
          runHistory: [
            {
              runId: 'r-ok',
              agentName: 'a',
              timestamp: '2026-02-01T00:00:00Z',
              outcome: 'succeeded',
              summary: '',
            },
            {
              runId: 'r-ko',
              agentName: 'b',
              timestamp: '2026-02-01T00:00:00Z',
              outcome: 'failed',
              summary: '',
            },
          ],
        }),
      );
      fixture = TestBed.createComponent(ProjectMemoryComponent);
      fixture.componentRef.setInput('projectId', 'p1');
      fixture.detectChanges();
      await settle();

      const rows = all('history-row');
      expect(rows[0].getAttribute('href')).toBe('/runs/r-ok');
      expect(rows[0].querySelector('span')!.className).toContain('green');
      expect(rows[1].querySelector('span')!.className).toContain('red');
    });
  });

  describe('live updates', () => {
    it('replaces the rendered snapshot when the hub pushes memoryLoaded', async () => {
      await setup('p1');
      expect(el('no-notes')).not.toBeNull();

      signalR.onMemoryLoaded$.next(
        projectMemory({
          notes: [
            { id: 'n1', agentName: 'veilleur', text: 'poussé en direct', timestamp: '2026-03-01T00:00:00Z' },
          ],
        }),
      );
      fixture.detectChanges();

      expect(el('no-notes')).toBeNull();
      expect(all('note-row')[0].textContent).toContain('poussé en direct');
      // Aucun aller-retour REST : le hub a fourni l'instantané complet.
      expect(projects.fetchMemory).toHaveBeenCalledTimes(1);
    });

    it('refetches over REST and re-renders when the hub signals memoryUpdated', async () => {
      await setup('p1');
      expect(projects.fetchMemory).toHaveBeenCalledTimes(1);

      projects.fetchMemory.mockResolvedValueOnce(
        projectMemory({
          patterns: [{ name: 'nouveau-motif', frequency: 1, lastSeen: '2026-03-01T00:00:00Z' }],
        }),
      );
      signalR.onMemoryUpdated$.next({ note: { text: 'x' } });
      await settle();

      expect(projects.fetchMemory).toHaveBeenCalledTimes(2);
      expect(projects.fetchMemory).toHaveBeenLastCalledWith('p1');
      expect(all('pattern-row')[0].textContent).toContain('nouveau-motif');
    });

    it('shows the error banner when the live refetch fails, keeping the stale card visible', async () => {
      await setup('p1');
      projects.fetchMemory.mockRejectedValueOnce(new Error('boom'));

      signalR.onMemoryUpdated$.next({});
      await settle();

      expect(el('memory-error')!.textContent).toContain('errors.loadMemory');
      expect(el('memory-context')).not.toBeNull();
    });

    it('leaves the hub group on destroy and stops reacting to pushes', async () => {
      await setup('p1');

      fixture.destroy();
      await Promise.resolve();

      expect(signalR.leaveProjectMemory).toHaveBeenCalledExactlyOnceWith('p1');

      projects.fetchMemory.mockClear();
      signalR.onMemoryUpdated$.next({});
      signalR.onMemoryLoaded$.next(projectMemory({ patterns: [] }));
      await Promise.resolve();

      expect(projects.fetchMemory).not.toHaveBeenCalled();
    });

    it('does not receive updates for a project it has switched away from', async () => {
      await setup('p1');
      fixture.componentRef.setInput('projectId', 'p2');
      fixture.detectChanges();
      await settle();
      projects.fetchMemory.mockClear();

      signalR.onMemoryUpdated$.next({});
      await settle();

      // Une seule souscription vivante, et elle cible bien le projet courant.
      expect(projects.fetchMemory).toHaveBeenCalledExactlyOnceWith('p2');
    });
  });

  describe('role gating', () => {
    it('is read-only whatever the role: no button, no form, no input', async () => {
      await setup('p1');

      expect(fixture.nativeElement.querySelectorAll('button').length).toBe(0);
      expect(fixture.nativeElement.querySelectorAll('form').length).toBe(0);
      expect(fixture.nativeElement.querySelectorAll('input, textarea').length).toBe(0);
    });
  });
});
