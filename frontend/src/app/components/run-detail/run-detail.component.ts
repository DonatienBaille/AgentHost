import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe, UpperCasePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { RunService } from '../../services/run.service';
import { SignalRService } from '../../services/signalr.service';
import { ArtifactService } from '../../services/artifact.service';
import { AuthService } from '../../services/auth.service';
import { Approval, Artifact, RunEvent, UserRole } from '../../core/models';
import { approvalBadgeClass, statusBadgeClass } from '../../core/utils/status';
import { hasRoleAtLeast } from '../../core/utils/roles';

/**
 * Run events that mean the run's state or its approvals changed. The hub only ever pushes
 * `RunEvent` (SignalREventBus.PublishAsync) — there is no live `runState` push after the initial
 * JoinRun — so these are what we key live refreshes off.
 */
const STATE_CHANGING_EVENTS = new Set([
  'run.status_changed',
  'approval.requested',
  'approval.approved',
  'approval.rejected',
  'question.asked',
  'question.answered',
]);

/** The role the server falls back to when an approval doesn't pin one (RunService.CallerMaySatisfy). */
const DEFAULT_REQUIRED_ROLE: UserRole = 'developer';

@Component({
  selector: 'app-run-detail',
  standalone: true,
  imports: [DatePipe, DecimalPipe, UpperCasePipe, FormsModule, TranslatePipe],
  templateUrl: './run-detail.component.html',
  styleUrls: ['./run-detail.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunDetailComponent implements OnInit, OnDestroy {
  private readonly runService = inject(RunService);
  private readonly signalRService = inject(SignalRService);
  private readonly artifactService = inject(ArtifactService);
  private readonly authService = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  readonly run = this.runService.currentRun;
  readonly events = signal<RunEvent[]>([]);
  readonly connectionState = this.signalRService.connectionState;

  readonly artifacts = this.artifactService.artifacts;
  readonly isLoadingArtifacts = this.artifactService.isLoading;
  readonly downloadingId = signal<string | null>(null);

  // --- human-in-the-loop state ---
  readonly approvals = signal<Approval[]>([]);
  readonly approveNote = signal('');
  readonly answerText = signal('');
  readonly isDeciding = signal(false);
  readonly decisionError = signal<string | null>(null);

  /** The gate/question the run is currently blocked on, if any. */
  readonly pendingApproval = computed<Approval | null>(
    () => this.approvals().find((a) => a.status === 'pending') ?? null,
  );

  readonly isAwaitingApproval = computed(() => this.run()?.status === 'awaiting_approval');
  readonly isAwaitingInput = computed(() => this.run()?.status === 'awaiting_input');
  readonly isBlocked = computed(() => this.isAwaitingApproval() || this.isAwaitingInput());

  /** Role the pending approval demands — shown to under-privileged users so they know who to ask. */
  readonly requiredRole = computed<UserRole>(
    () => this.pendingApproval()?.requiredRole ?? DEFAULT_REQUIRED_ROLE,
  );

  /**
   * Whether the current user may decide. The server enforces the same rule and refuses otherwise,
   * so the UI must not offer an action that is guaranteed to fail.
   */
  readonly canDecide = computed(() =>
    hasRoleAtLeast(this.authService.currentUser()?.role, this.requiredRole()),
  );

  /** The agent's `options` when it is a plain list of answers; empty otherwise (free-text). */
  readonly approvalOptions = computed<string[]>(() => {
    const options = this.pendingApproval()?.options;
    if (!Array.isArray(options)) return [];
    return options
      .filter((o) => o !== null && typeof o !== 'object')
      .map((o) => String(o));
  });

  private subs: Subscription[] = [];
  private runId: string | null = null;

  ngOnInit(): void {
    this.route.params.subscribe((params) => {
      const id = params['id'];
      if (id) {
        this.runId = id;
        this.runService.selectRun(id);
        this.loadInitialEvents(id);
        this.loadApprovals(id);
        this.joinLiveRun(id);
        this.artifactService.listArtifacts(id).catch((err) => console.error(err));
      }
    });
  }

  ngOnDestroy(): void {
    if (this.runId) {
      this.signalRService.leaveRun(this.runId).catch(() => undefined);
    }
    this.subs.forEach((s) => s.unsubscribe());
    this.signalRService.disconnect();
  }

  private async loadInitialEvents(runId: string): Promise<void> {
    try {
      const events = await this.runService.fetchEvents(runId, 0);
      this.events.set(events);
    } catch {
      // surfaced via runService.error already
    }
  }

  private async loadApprovals(runId: string): Promise<void> {
    try {
      this.approvals.set(await this.runService.fetchApprovals(runId));
    } catch {
      // surfaced globally by the error interceptor
    }
  }

  private async joinLiveRun(runId: string): Promise<void> {
    await this.signalRService.connect();
    await this.signalRService.joinRun(runId);

    this.subs.push(
      this.signalRService.onRunEvent$.subscribe((evt) => {
        if (evt.runId !== runId) return;
        this.events.set([...this.events(), evt].slice(-200));

        // Reflect approval/state transitions live: refetch the run and its approvals whenever an
        // event says either changed.
        if (STATE_CHANGING_EVENTS.has(evt.eventType)) {
          this.runService.fetchRun(runId).catch(() => undefined);
          this.loadApprovals(runId);
        }
      }),
      this.signalRService.onRunState$.subscribe((run) => {
        this.runService.applyRunState(run);
      }),
      // A decision made by *another* operator on the same run.
      this.signalRService.onStepApproved$.subscribe(() => this.refreshBlockedState(runId)),
      this.signalRService.onQuestionAnswered$.subscribe(() => this.refreshBlockedState(runId)),
    );
  }

  private refreshBlockedState(runId: string): void {
    this.runService.fetchRun(runId).catch(() => undefined);
    this.loadApprovals(runId);
  }

  onAnswerInput(value: string): void {
    this.answerText.set(value);
  }

  selectOption(option: string): void {
    this.answerText.set(option);
  }

  /** Approve or reject the pending gate. `stepId` is echoed back to the agent by the server. */
  async decide(decision: 'approve' | 'reject'): Promise<void> {
    const runId = this.run()?.id;
    if (!runId || !this.canDecide() || this.isDeciding()) return;

    this.isDeciding.set(true);
    this.decisionError.set(null);
    try {
      await this.runService.approveRun(runId, {
        stepId: this.pendingApproval()?.stepId ?? undefined,
        decision,
        note: this.approveNote() || undefined,
      });
      this.approveNote.set('');
      await this.loadApprovals(runId);
    } catch {
      this.decisionError.set('runDetail.decisionFailed');
    } finally {
      this.isDeciding.set(false);
    }
  }

  /** Answer the pending question. The approval's own id doubles as the question id. */
  async submitAnswer(): Promise<void> {
    const runId = this.run()?.id;
    const approval = this.pendingApproval();
    const answer = this.answerText().trim();
    if (!runId || !approval || !answer || !this.canDecide() || this.isDeciding()) return;

    this.isDeciding.set(true);
    this.decisionError.set(null);
    try {
      await this.runService.answerQuestion(runId, approval.id, answer);
      this.answerText.set('');
      await this.loadApprovals(runId);
    } catch {
      this.decisionError.set('runDetail.answerFailed');
    } finally {
      this.isDeciding.set(false);
    }
  }

  cancelRun(): void {
    const runId = this.run()?.id;
    if (runId) {
      this.runService.cancelRun(runId).catch((err) => console.error(err));
    }
  }

  statusBadgeClass(status: string): string {
    return statusBadgeClass(status);
  }

  approvalBadgeClass(status: string): string {
    return approvalBadgeClass(status);
  }

  formatBytes(bytes: number | null): string {
    if (bytes === null || bytes === undefined) return '-';
    if (bytes < 1024) return `${bytes} B`;
    const units = ['KB', 'MB', 'GB', 'TB'];
    let value = bytes / 1024;
    let unitIndex = 0;
    while (value >= 1024 && unitIndex < units.length - 1) {
      value /= 1024;
      unitIndex++;
    }
    return `${value.toFixed(1)} ${units[unitIndex]}`;
  }

  async downloadArtifact(artifact: Artifact): Promise<void> {
    this.downloadingId.set(artifact.id);
    try {
      await this.artifactService.download(artifact);
    } catch (err) {
      console.error(err);
    } finally {
      this.downloadingId.set(null);
    }
  }
}
