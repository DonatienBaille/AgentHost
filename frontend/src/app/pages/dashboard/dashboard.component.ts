import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
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
  readonly successRate = this.runService.successRate;
  readonly failedRuns = this.runService.failedRuns;
  readonly activeRuns = this.runService.activeRuns;
  readonly runCount = this.runService.runCount;

  readonly projects = this.projectService.projects;

  /**
   * La page charge deux ressources indépendantes ; n'en afficher qu'une erreur cachait l'autre.
   * Un échec de chargement des projets laissait le panneau afficher son état vide, indiscernable
   * d'une organisation qui n'a réellement aucun projet.
   */
  readonly error = computed(() => this.runService.error() ?? this.projectService.error());

  /** Vraie quand les projets ont échoué : le panneau doit le dire au lieu de se dire vide. */
  readonly projectsFailed = computed(() => this.projectService.error() !== null);

  ngOnInit(): void {
    this.runService.listRuns(0, 20);
    this.projectService.listProjects();
  }

  recentRuns() {
    // `localeCompare` plutôt qu'un ternaire : le comparateur précédent ne renvoyait jamais 0, donc
    // deux runs de même horodatage — le cas courant, plusieurs runs lancés d'un coup — obtenaient
    // un ordre dépendant de l'implémentation du tri.
    return [...this.runs()]
      .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
      .slice(0, 8);
  }

  statusBadgeClass(status: string): string {
    return statusBadgeClass(status);
  }
}
