import { Injectable, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { ProjectService } from './project.service';
import { SignalRService } from './signalr.service';
import { MemoryUpdate, ProjectMemory } from '../core/models';

/**
 * Thin wrapper combining REST (ProjectService) and live updates (AgentMemoryHub)
 * for the project-memory component.
 */
@Injectable({ providedIn: 'root' })
export class MemoryService {
  private readonly projectService = inject(ProjectService);
  private readonly signalR = inject(SignalRService);

  readonly memory = signal<ProjectMemory | null>(null);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  private subs: Subscription[] = [];
  private currentProjectId: string | null = null;

  /** Loads memory over REST, then joins the live memory hub group for updates. */
  async load(projectId: string): Promise<void> {
    // Loading a second project must give up the first one's group, otherwise the connection keeps
    // receiving `memoryUpdated` for a project the UI no longer shows — and every one of those
    // broadcasts triggers a refetch of the *current* project, i.e. pure noise.
    const previous = this.currentProjectId;
    if (previous && previous !== projectId) {
      await this.leaveGroup(previous);
    }

    this.currentProjectId = projectId;
    this.isLoading.set(true);
    try {
      const memory = await this.projectService.fetchMemory(projectId);
      this.memory.set(memory);
      this.error.set(null);
    } catch (err) {
      this.error.set('errors.loadMemory');
    } finally {
      this.isLoading.set(false);
    }

    this.subscribeToLive();
    await this.signalR.joinProjectMemory(projectId);
  }

  async pushUpdate(update: MemoryUpdate): Promise<void> {
    if (!this.currentProjectId) return;
    await this.signalR.updateMemory(this.currentProjectId, update);
  }

  /**
   * Tears the service back down to its pre-`load` state: local subscriptions dropped, the hub
   * group left, and no current project — so a stray `pushUpdate` can no longer write into whatever
   * project happened to be loaded last.
   *
   * Stays synchronous because its caller is `ngOnDestroy`. The group departure is therefore
   * fire-and-forget; it is a best-effort courtesy (the server drops the membership anyway when the
   * connection closes), so a failure is swallowed rather than surfaced to a component that is
   * already gone.
   */
  dispose(): void {
    const projectId = this.currentProjectId;
    this.currentProjectId = null;
    this.unsubscribeLocal();
    if (projectId) {
      void this.leaveGroup(projectId);
    }
  }

  private async leaveGroup(projectId: string): Promise<void> {
    try {
      await this.signalR.leaveProjectMemory(projectId);
    } catch {
      // Nothing actionable: the membership dies with the connection regardless.
    }
  }

  private unsubscribeLocal(): void {
    this.subs.forEach((s) => s.unsubscribe());
    this.subs = [];
  }

  private subscribeToLive(): void {
    this.unsubscribeLocal();
    this.subs.push(
      this.signalR.onMemoryLoaded$.subscribe((memory) => this.memory.set(memory)),
      this.signalR.onMemoryUpdated$.subscribe(() => {
        // Server broadcasts the raw update; refresh from REST for a consistent full snapshot.
        const projectId = this.currentProjectId;
        if (!projectId) return;
        this.projectService
          .fetchMemory(projectId)
          .then((memory) => {
            this.memory.set(memory);
            this.error.set(null);
          })
          // Without this the failed refetch is an unhandled rejection: the UI would keep showing
          // the pre-update snapshot with no indication that it is now stale.
          .catch(() => this.error.set('errors.loadMemory'));
      }),
    );
  }
}
