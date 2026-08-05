import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { RunService } from '../../services/run.service';
import { ProjectService } from '../../services/project.service';
import { statusBadgeClass } from '../../core/utils/status';

@Component({
  selector: 'app-dashboard',
  standalone: true,
  imports: [DatePipe, DecimalPipe, RouterLink, TranslatePipe],
  templateUrl: './dashboard.component.html',
  styleUrls: ['./dashboard.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class DashboardComponent implements OnInit {
  private readonly runService = inject(RunService);
  private readonly projectService = inject(ProjectService);

  readonly runs = this.runService.runs;
  readonly isLoading = this.runService.isLoading;
  readonly error = this.runService.error;
  readonly successRate = this.runService.successRate;
  readonly failedRuns = this.runService.failedRuns;
  readonly activeRuns = this.runService.activeRuns;
  readonly runCount = this.runService.runCount;

  readonly projects = this.projectService.projects;

  ngOnInit(): void {
    this.runService.listRuns(0, 20);
    this.projectService.listProjects();
  }

  recentRuns() {
    return [...this.runs()]
      .sort((a, b) => (a.createdAt < b.createdAt ? 1 : -1))
      .slice(0, 8);
  }

  statusBadgeClass(status: string): string {
    return statusBadgeClass(status);
  }
}
