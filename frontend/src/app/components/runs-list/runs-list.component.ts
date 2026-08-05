import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { RunService } from '../../services/run.service';
import { statusBadgeClass } from '../../core/utils/status';

@Component({
  selector: 'app-runs-list',
  standalone: true,
  imports: [DatePipe, DecimalPipe, RouterLink, TranslatePipe],
  templateUrl: './runs-list.component.html',
  styleUrls: ['./runs-list.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunsListComponent implements OnInit {
  private readonly runService = inject(RunService);

  // Expose signals depuis le service
  readonly runs = this.runService.runs;
  readonly isLoading = this.runService.isLoading;
  readonly error = this.runService.error;
  readonly successRate = this.runService.successRate;
  readonly failedRuns = this.runService.failedRuns;

  ngOnInit(): void {
    this.runService.listRuns();
  }

  selectRun(id: string): void {
    this.runService.selectRun(id);
  }

  reload(): void {
    this.runService.listRuns();
  }

  statusBadgeClass(status: string): string {
    return statusBadgeClass(status);
  }
}
