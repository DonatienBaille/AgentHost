import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { MemoryService } from './memory.service';
import { ProjectService } from './project.service';
import { SignalRService } from './signalr.service';
import { MemoryUpdate, ProjectMemory } from '../core/models';

function memory(projectId: string, note = 'a'): ProjectMemory {
  return {
    projectId,
    context: { name: 'P', description: '', technologies: [], recentDecisions: [] },
    runHistory: [],
    patterns: [],
    learnings: [],
    notes: [{ id: 'n1', agentName: 'x', text: note, timestamp: '2026-01-01T00:00:00Z' }],
    archived: [],
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

/** Stubs, so this spec exercises MemoryService's own orchestration and nothing else. */
class ProjectServiceStub {
  fetchMemory = vi.fn(async (id: string) => memory(id));
}

class SignalRServiceStub {
  readonly onMemoryLoaded$ = new Subject<ProjectMemory>();
  readonly onMemoryUpdated$ = new Subject<MemoryUpdate>();
  joinProjectMemory = vi.fn(async (_projectId: string) => {});
  leaveProjectMemory = vi.fn(async (_projectId: string) => {});
  updateMemory = vi.fn(async (_projectId: string, _update: MemoryUpdate) => {});
}

describe('MemoryService', () => {
  let service: MemoryService;
  let projects: ProjectServiceStub;
  let signalR: SignalRServiceStub;

  beforeEach(() => {
    projects = new ProjectServiceStub();
    signalR = new SignalRServiceStub();
    TestBed.configureTestingModule({
      providers: [
        { provide: ProjectService, useValue: projects },
        { provide: SignalRService, useValue: signalR },
      ],
    });
    service = TestBed.inject(MemoryService);
  });

  afterEach(() => service.dispose());

  it('loads over REST then joins the live memory group', async () => {
    await service.load('p1');

    expect(projects.fetchMemory).toHaveBeenCalledWith('p1');
    expect(signalR.joinProjectMemory).toHaveBeenCalledWith('p1');
    expect(service.memory()?.projectId).toBe('p1');
    expect(service.error()).toBeNull();
    expect(service.isLoading()).toBe(false);
  });

  it('still joins the live group when the REST load fails, and records the error', async () => {
    projects.fetchMemory.mockRejectedValueOnce(new Error('boom'));

    await service.load('p1');

    expect(service.error()).toBe('errors.loadMemory');
    expect(service.memory()).toBeNull();
    expect(service.isLoading()).toBe(false);
    expect(signalR.joinProjectMemory).toHaveBeenCalledWith('p1');
  });

  it('replaces the cached memory when the hub pushes a full snapshot', async () => {
    await service.load('p1');

    signalR.onMemoryLoaded$.next(memory('p1', 'pushed'));

    expect(service.memory()?.notes[0].text).toBe('pushed');
  });

  it('refetches over REST when the hub signals an update', async () => {
    await service.load('p1');
    expect(projects.fetchMemory).toHaveBeenCalledTimes(1);

    projects.fetchMemory.mockResolvedValueOnce(memory('p1', 'refetched'));
    signalR.onMemoryUpdated$.next({ note: { text: 'x' } });
    await Promise.resolve();
    await Promise.resolve();

    expect(projects.fetchMemory).toHaveBeenCalledTimes(2);
    expect(service.memory()?.notes[0].text).toBe('refetched');
  });

  it('does not double-subscribe when load is called twice', async () => {
    await service.load('p1');
    await service.load('p1');
    projects.fetchMemory.mockClear();

    signalR.onMemoryUpdated$.next({});
    await Promise.resolve();
    await Promise.resolve();

    // One live subscription only — a second would refetch twice per broadcast.
    expect(projects.fetchMemory).toHaveBeenCalledTimes(1);
  });

  it('pushUpdate targets the most recently loaded project', async () => {
    await service.load('p1');
    await service.load('p2');

    await service.pushUpdate({ note: { text: 'hi' } });

    expect(signalR.updateMemory).toHaveBeenCalledWith('p2', { note: { text: 'hi' } });
  });

  it('pushUpdate is a no-op before any project is loaded', async () => {
    await service.pushUpdate({ note: { text: 'hi' } });
    expect(signalR.updateMemory).not.toHaveBeenCalled();
  });

  it('dispose stops reacting to hub pushes', async () => {
    await service.load('p1');
    service.dispose();

    signalR.onMemoryLoaded$.next(memory('p1', 'ignored'));

    expect(service.memory()?.notes[0].text).toBe('a');
  });

  it('dispose leaves the hub group and forgets the project', async () => {
    await service.load('p1');
    service.dispose();
    await Promise.resolve();

    expect(signalR.leaveProjectMemory).toHaveBeenCalledWith('p1');

    // Forgetting matters: a pushUpdate that survives teardown would otherwise write into whichever
    // project happened to be loaded last.
    await service.pushUpdate({ note: { text: 'stray' } });
    expect(signalR.updateMemory).not.toHaveBeenCalled();
  });

  it('dispose is a no-op before any project is loaded', () => {
    service.dispose();
    expect(signalR.leaveProjectMemory).not.toHaveBeenCalled();
  });

  it('a failing group departure does not escape dispose', async () => {
    signalR.leaveProjectMemory.mockRejectedValueOnce(new Error('socket gone'));
    await service.load('p1');

    expect(() => service.dispose()).not.toThrow();
    await Promise.resolve();
  });

  it('switching projects gives up the previous group', async () => {
    await service.load('p1');
    await service.load('p2');

    expect(signalR.leaveProjectMemory).toHaveBeenCalledExactlyOnceWith('p1');
    expect(signalR.joinProjectMemory).toHaveBeenNthCalledWith(2, 'p2');
  });

  it('reloading the same project does not churn the group membership', async () => {
    await service.load('p1');
    await service.load('p1');

    expect(signalR.leaveProjectMemory).not.toHaveBeenCalled();
  });

  it('surfaces a failed refetch instead of dropping it as an unhandled rejection', async () => {
    await service.load('p1');
    projects.fetchMemory.mockRejectedValueOnce(new Error('boom'));

    signalR.onMemoryUpdated$.next({ note: { text: 'x' } });
    await Promise.resolve();
    await Promise.resolve();

    expect(service.error()).toBe('errors.loadMemory');
    // The stale snapshot is still on screen — which is exactly why the error must be visible.
    expect(service.memory()?.notes[0].text).toBe('a');
  });
});
