import { ChangeDetectionStrategy, Component, OnInit, inject } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { RunService } from '../../../services/run.service';
import { statusBadgeClass } from '../../../core/utils/status';

@Component({
  selector: 'app-runs-page',
  standalone: true,
  imports: [DatePipe, DecimalPipe, RouterLink, TranslatePipe],
  templateUrl: './runs-page.component.html',
  styleUrls: ['./runs-page.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RunsPageComponent implements OnInit {
  private readonly runService = inject(RunService);

  readonly runs = this.runService.runs;
  readonly isLoading = this.runService.isLoading;
  readonly error = this.runService.error;
  readonly runCount = this.runService.runCount;
  readonly successRate = this.runService.successRate;
  readonly failedRuns = this.runService.failedRuns;

  ngOnInit(): void {
    this.runService.listRuns(0, 100);
  }

  statusBadgeClass(status: string): string {
    return statusBadgeClass(status);
  }
}
