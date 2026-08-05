import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe, DecimalPipe, UpperCasePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { RunService } from '../../services/run.service';
import { SignalRService } from '../../services/signalr.service';
import { ArtifactService } from '../../services/artifact.service';
import { Artifact, RunEvent } from '../../core/models';
import { statusBadgeClass } from '../../core/utils/status';

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
  private readonly route = inject(ActivatedRoute);

  readonly run = this.runService.currentRun;
  readonly events = signal<RunEvent[]>([]);
  readonly connectionState = this.signalRService.connectionState;
  readonly approveNote = signal('');

  readonly artifacts = this.artifactService.artifacts;
  readonly isLoadingArtifacts = this.artifactService.isLoading;
  readonly downloadingId = signal<string | null>(null);

  /** stepId of the latest pending approval request found in the event stream, if any. */
  readonly pendingStepId = computed(() => {
    const evts = this.events();
    for (let i = evts.length - 1; i >= 0; i--) {
      const evt = evts[i];
      if (evt.eventType === 'approval.requested') {
        const payload = evt.payload as { stepId?: string } | null;
        return payload?.stepId ?? '';
      }
    }
    return '';
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

  private async joinLiveRun(runId: string): Promise<void> {
    await this.signalRService.connect();
    await this.signalRService.joinRun(runId);

    this.subs.push(
      this.signalRService.onRunEvent$.subscribe((evt) => {
        if (evt.runId !== runId) return;
        const current = this.events();
        this.events.set([...current, evt].slice(-200));
      }),
      this.signalRService.onRunState$.subscribe((run) => {
        this.runService.applyRunState(run);
      }),
    );
  }

  approveStep(stepId: string): void {
    const runId = this.run()?.id;
    if (runId) {
      this.signalRService.approveStep(runId, stepId, this.approveNote() || 'Approved from UI');
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
