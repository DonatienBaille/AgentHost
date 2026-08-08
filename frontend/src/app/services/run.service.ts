import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { clampSkip, clampTake } from '../core/utils/paging';
import {
  AnswerQuestionRequest,
  Approval,
  ApprovalRequest,
  CreateRunRequest,
  Run,
  RunEvent,
  RunTree,
} from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/runs`;

@Injectable({ providedIn: 'root' })
export class RunService {
  private readonly http = inject(HttpClient);

  // State signals
  readonly runs = signal<Run[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly currentRunId = signal<string | null>(null);

  /** L'arbre de chaînage du run affiché, quand il en a un (lot 4). */
  readonly runTree = signal<RunTree | null>(null);

  // Computed (reactive, derived from signals)
  readonly currentRun = computed(() => {
    const id = this.currentRunId();
    return id ? (this.runs().find((r) => r.id === id) ?? null) : null;
  });

  readonly runCount = computed(() => this.runs().length);

  readonly failedRuns = computed(
    () =>
      this.runs().filter((r) =>
        ['failed', 'rejected', 'timed_out', 'budget_exceeded', 'infra_error'].includes(r.status),
      ).length,
  );

  readonly activeRuns = computed(
    () =>
      this.runs().filter((r) =>
        [
          'pending',
          'queued',
          'provisioning',
          'preparing',
          'running',
          'awaiting_approval',
          'awaiting_input',
          'finalizing',
        ].includes(r.status),
      ).length,
  );

  readonly successRate = computed(() => {
    const total = this.runs().length;
    const succeeded = this.runs().filter((r) => r.status === 'succeeded').length;
    return total > 0 ? (succeeded / total) * 100 : 0;
  });

  constructor() {
    // Effect: auto-refetch quand currentRunId change
    effect(() => {
      const id = this.currentRunId();
      if (id) {
        this.fetchRun(id).catch((err) => console.error(err));
      }
    });
  }

  async listRuns(skip = 0, take = 50): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(
        this.http.get<Run[]>(`${BASE_URL}?skip=${clampSkip(skip)}&take=${clampTake(take)}`),
      );
      this.runs.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('errors.loadRuns');
    } finally {
      this.isLoading.set(false);
    }
  }

  async fetchRun(id: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const run = await firstValueFrom(this.http.get<Run>(`${BASE_URL}/${id}`));
      this.upsertRun(run);
      this.error.set(null);
    } catch (err) {
      this.error.set('errors.loadRun');
    } finally {
      this.isLoading.set(false);
    }
  }

  /**
   * L'arbre de chaînage auquel ce run appartient (feuille de route, lot 4).
   *
   * Un échec n'est pas signalé comme une erreur de page : l'arbre est un complément à la fiche du
   * run, et le faire apparaître en rouge laisserait croire que le run lui-même n'a pas pu être lu.
   * Sans arbre, le panneau ne s'affiche simplement pas.
   */
  async fetchRunTree(id: string): Promise<void> {
    try {
      this.runTree.set(await firstValueFrom(this.http.get<RunTree>(`${BASE_URL}/${id}/tree`)));
    } catch {
      this.runTree.set(null);
    }
  }

  async createRun(req: CreateRunRequest): Promise<Run> {
    this.isLoading.set(true);
    try {
      const run = await firstValueFrom(this.http.post<Run>(BASE_URL, req));
      this.upsertRun(run);
      this.error.set(null);
      return run;
    } catch (err) {
      this.error.set('errors.createRun');
      throw err;
    } finally {
      this.isLoading.set(false);
    }
  }

  async cancelRun(id: string): Promise<void> {
    try {
      await firstValueFrom(this.http.post<void>(`${BASE_URL}/${id}/cancel`, {}));
      await this.fetchRun(id);
    } catch (err) {
      this.error.set('errors.cancelRun');
      throw err;
    }
  }

  /** Approves or rejects the run's pending gate (POST /api/runs/{id}/approve). */
  async approveRun(id: string, req: ApprovalRequest): Promise<void> {
    try {
      await firstValueFrom(this.http.post<void>(`${BASE_URL}/${id}/approve`, req));
      await this.fetchRun(id);
    } catch (err) {
      this.error.set('errors.approveRun');
      throw err;
    }
  }

  /** Answers the run's pending question (POST /api/runs/{id}/answer). */
  async answerQuestion(id: string, questionId: string, answer: string): Promise<void> {
    const body: AnswerQuestionRequest = { questionId, answer };
    try {
      await firstValueFrom(this.http.post<void>(`${BASE_URL}/${id}/answer`, body));
      await this.fetchRun(id);
    } catch (err) {
      this.error.set('errors.answerQuestion');
      throw err;
    }
  }

  /** Every approval ever raised on a run (GET /api/runs/{runId}/approvals). */
  async fetchApprovals(id: string): Promise<Approval[]> {
    return await firstValueFrom(this.http.get<Approval[]>(`${BASE_URL}/${id}/approvals`));
  }

  async fetchEvents(id: string, fromSeq = 0): Promise<RunEvent[]> {
    try {
      return await firstValueFrom(
        this.http.get<RunEvent[]>(`${BASE_URL}/${id}/events?fromSeq=${fromSeq}`),
      );
    } catch (err) {
      this.error.set('errors.loadRunEvents');
      throw err;
    }
  }

  async fetchLogs(id: string): Promise<string> {
    const res = await firstValueFrom(
      this.http.get<{ logs: string }>(`${BASE_URL}/${id}/logs`),
    );
    return res.logs;
  }

  selectRun(id: string): void {
    this.currentRunId.set(id);
  }

  /** Merges a run event pushed by SignalR into local state (e.g. status/outputs updates). */
  applyRunState(run: Run): void {
    this.upsertRun(run);
  }

  private upsertRun(run: Run): void {
    const runs = this.runs();
    const index = runs.findIndex((r) => r.id === run.id);
    if (index >= 0) {
      const copy = [...runs];
      copy[index] = run;
      this.runs.set(copy);
    } else {
      this.runs.set([...runs, run]);
    }
  }
}
