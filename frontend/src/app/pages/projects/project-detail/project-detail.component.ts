import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { Subscription } from 'rxjs';
import { ProjectService } from '../../../services/project.service';
import { SignalRService } from '../../../services/signalr.service';
import { ProjectMemoryComponent } from '../../../components/project-memory/project-memory.component';
import { Run } from '../../../core/models';
import { statusBadgeClass } from '../../../core/utils/status';

@Component({
  selector: 'app-project-detail',
  standalone: true,
  imports: [DatePipe, RouterLink, TranslatePipe, ProjectMemoryComponent],
  templateUrl: './project-detail.component.html',
  styleUrls: ['./project-detail.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDetailComponent implements OnInit, OnDestroy {
  private readonly projectService = inject(ProjectService);
  private readonly signalRService = inject(SignalRService);
  private readonly route = inject(ActivatedRoute);

  readonly project = this.projectService.currentProject;
  readonly isLoading = this.projectService.isLoading;
  readonly error = this.projectService.error;
  readonly recentRuns = signal<Run[]>([]);

  readonly projectId = computed(() => this.project()?.id ?? this.routeProjectId());
  private readonly routeProjectId = signal<string>('');

  private subs: Subscription[] = [];

  ngOnInit(): void {
    this.route.params.subscribe((params) => {
      const id = params['id'];
      if (id) {
        this.routeProjectId.set(id);
        this.projectService.selectProject(id);
        this.projectService.fetchProject(id);
        this.joinProjectGroup(id);
      }
    });
  }

  ngOnDestroy(): void {
    const id = this.routeProjectId();
    if (id) {
      this.signalRService.leaveProject(id).catch(() => undefined);
    }
    this.subs.forEach((s) => s.unsubscribe());
    this.signalRService.disconnectProject();
  }

  private async joinProjectGroup(projectId: string): Promise<void> {
    this.subs.forEach((s) => s.unsubscribe());
    this.subs = [
      this.signalRService.onRecentRuns$.subscribe((runs) => this.recentRuns.set(runs)),
    ];
    await this.signalRService.joinProject(projectId);
  }

  statusBadgeClass(status: string): string {
    return statusBadgeClass(status);
  }
}
