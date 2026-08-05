import { ChangeDetectionStrategy, Component, OnDestroy, effect, inject, input } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { MemoryService } from '../../services/memory.service';
import { statusBadgeClass } from '../../core/utils/status';

@Component({
  selector: 'app-project-memory',
  standalone: true,
  imports: [DatePipe, RouterLink, TranslatePipe],
  templateUrl: './project-memory.component.html',
  styleUrls: ['./project-memory.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectMemoryComponent implements OnDestroy {
  private readonly memoryService = inject(MemoryService);

  /** Project id supplied by the host page (e.g. ProjectDetailComponent). */
  readonly projectId = input.required<string>();

  readonly memory = this.memoryService.memory;
  readonly isLoading = this.memoryService.isLoading;
  readonly error = this.memoryService.error;

  constructor() {
    effect(() => {
      const id = this.projectId();
      if (id) {
        this.memoryService.load(id);
      }
    });
  }

  ngOnDestroy(): void {
    this.memoryService.dispose();
  }

  outcomeBadgeClass(outcome: string): string {
    return statusBadgeClass(outcome);
  }
}
