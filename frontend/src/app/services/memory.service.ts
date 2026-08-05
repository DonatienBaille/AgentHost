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
    this.currentProjectId = projectId;
    this.isLoading.set(true);
    try {
      const memory = await this.projectService.fetchMemory(projectId);
      this.memory.set(memory);
      this.error.set(null);
    } catch (err) {
      this.error.set(`Failed to load memory for project ${projectId}`);
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

  dispose(): void {
    this.subs.forEach((s) => s.unsubscribe());
    this.subs = [];
  }

  private subscribeToLive(): void {
    this.dispose();
    this.subs.push(
      this.signalR.onMemoryLoaded$.subscribe((memory) => this.memory.set(memory)),
      this.signalR.onMemoryUpdated$.subscribe(() => {
        // Server broadcasts the raw update; refresh from REST for a consistent full snapshot.
        if (this.currentProjectId) {
          this.projectService.fetchMemory(this.currentProjectId).then((memory) => {
            this.memory.set(memory);
          });
        }
      }),
    );
  }
}
