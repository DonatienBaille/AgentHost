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
      this.error.set('Failed to load runs');
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
      this.error.set(`Failed to load run ${id}`);
    } finally {
      this.isLoading.set(false);
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
      this.error.set('Failed to create run');
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
      this.error.set(`Failed to cancel run ${id}`);
      throw err;
    }
  }

  /** Approves or rejects the run's pending gate (POST /api/runs/{id}/approve). */
  async approveRun(id: string, req: ApprovalRequest): Promise<void> {
    try {
      await firstValueFrom(this.http.post<void>(`${BASE_URL}/${id}/approve`, req));
      await this.fetchRun(id);
    } catch (err) {
      this.error.set(`Failed to approve run ${id}`);
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
      this.error.set(`Failed to answer question for run ${id}`);
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
      this.error.set(`Failed to load events for run ${id}`);
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
