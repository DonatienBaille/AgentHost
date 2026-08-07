import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { MetricsService } from '../../services/metrics.service';
import { DailyPoint, ProjectUsage } from '../../core/models';

/** Une alerte affichée en tête de page, avec de quoi la rendre sans deviner. */
export interface MonitoringAlert {
  /** Clé i18n du message. */
  key: string;
  /** Paramètres d'interpolation du message. */
  params?: Record<string, string | number>;
  /** `danger` = un seuil est franchi ; `warning` = il va l'être. */
  level: 'danger' | 'warning';
  testId: string;
}

/**
 * Le tableau de bord de supervision (feuille de route, lot 3).
 *
 * Il répond aux quatre questions qu'un owner se pose et auxquelles rien ne répondait depuis l'IHM :
 * combien mes agents m'ont coûté, lesquels échouent, où en est mon budget, et qu'est-ce qui attend.
 *
 * <b>Les alertes sont la partie utile.</b> Un tableau de chiffres demande à être lu et interprété ;
 * une alerte dit ce qui ne va pas. Les trois seuils ci-dessous sont dérivés des données déjà
 * servies — aucun réglage à configurer, donc rien à oublier de configurer.
 *
 * <b>Aucune dépendance de graphique.</b> La série quotidienne est rendue en barres CSS : ajouter
 * une bibliothèque de graphiques pour sept barres coûterait plus que ça ne rapporte, et la lecture
 * ne serait pas meilleure.
 */
@Component({
  selector: 'app-monitoring',
  standalone: true,
  imports: [DecimalPipe, RouterLink, TranslatePipe],
  templateUrl: './monitoring.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MonitoringComponent implements OnInit {
  private readonly metrics = inject(MetricsService);

  readonly overview = this.metrics.overview;
  readonly byAgent = this.metrics.byAgent;
  readonly byProject = this.metrics.byProject;
  readonly daily = this.metrics.daily;
  readonly isLoading = this.metrics.isLoading;
  readonly error = this.metrics.error;
  readonly windowDays = this.metrics.windowDays;
  readonly successRate = this.metrics.successRate;

  /** Fenêtres proposées. Le serveur plafonne à 90 ; on ne propose donc rien au-delà. */
  readonly windows = [7, 30, 90];

  /** Part du budget consommée au-delà de laquelle on prévient, puis au-delà de laquelle on alerte. */
  private static readonly BudgetWarnRatio = 0.8;

  /** Taux d'échec au-delà duquel un agent est signalé, à partir d'un volume qui fait sens. */
  private static readonly FailureRateThreshold = 0.5;
  private static readonly MinRunsForFailureAlert = 5;

  /** Une approbation qui attend plus longtemps que ça n'attend plus : elle est oubliée. */
  private static readonly PendingApprovalAlertSeconds = 24 * 3600;

  readonly selectedWindow = signal(30);

  /**
   * Ce qui mérite d'interrompre la lecture. Ordonné du plus grave au moins grave : personne ne lit
   * la troisième ligne d'un bandeau d'alertes.
   */
  readonly alerts = computed<MonitoringAlert[]>(() => {
    const alerts: MonitoringAlert[] = [];
    const overview = this.overview();

    for (const project of this.byProject()) {
      const ratio = MonitoringComponent.budgetRatio(project);
      if (ratio === null) continue;

      if (ratio >= 1) {
        alerts.push({
          key: 'monitoring.alerts.budgetExceeded',
          params: { project: project.name, percent: Math.round(ratio * 100) },
          level: 'danger',
          testId: 'alert-budget-exceeded',
        });
      } else if (ratio >= MonitoringComponent.BudgetWarnRatio) {
        alerts.push({
          key: 'monitoring.alerts.budgetNear',
          params: { project: project.name, percent: Math.round(ratio * 100) },
          level: 'warning',
          testId: 'alert-budget-near',
        });
      }
    }

    for (const agent of this.byAgent()) {
      if (agent.runs < MonitoringComponent.MinRunsForFailureAlert) continue;

      const failureRate = (agent.runs - agent.succeeded) / agent.runs;
      if (failureRate > MonitoringComponent.FailureRateThreshold) {
        alerts.push({
          key: 'monitoring.alerts.failureRate',
          params: { agent: agent.name, percent: Math.round(failureRate * 100) },
          level: 'danger',
          testId: 'alert-failure-rate',
        });
      }
    }

    if (
      overview &&
      overview.oldestPendingApprovalSeconds >= MonitoringComponent.PendingApprovalAlertSeconds
    ) {
      alerts.push({
        key: 'monitoring.alerts.approvalStale',
        params: { hours: Math.floor(overview.oldestPendingApprovalSeconds / 3600) },
        level: 'warning',
        testId: 'alert-approval-stale',
      });
    }

    return alerts.sort((a, b) => (a.level === b.level ? 0 : a.level === 'danger' ? -1 : 1));
  });

  /**
   * Hauteur de chaque barre, en pourcentage du plus haut point de la série.
   *
   * Relative et non absolue : sans échelle commune, une journée à 3 runs et une journée à 300
   * seraient rendues à l'identique. Un jour à zéro garde une barre visible d'un pixel, pour qu'il
   * se distingue d'une absence de donnée.
   */
  readonly chartBars = computed(() => {
    const points = this.daily();
    const peak = Math.max(1, ...points.map((p) => p.runs));
    return points.map((point) => ({
      point,
      heightPercent: point.runs === 0 ? 1 : Math.max(4, (point.runs / peak) * 100),
    }));
  });

  ngOnInit(): void {
    void this.metrics.load(this.selectedWindow());
  }

  async selectWindow(days: number): Promise<void> {
    this.selectedWindow.set(days);
    await this.metrics.load(days);
  }

  async refresh(): Promise<void> {
    await this.metrics.load(this.selectedWindow());
  }

  /** Part du budget mensuel consommée, ou null quand aucun plafond n'est fixé. */
  static budgetRatio(project: ProjectUsage): number | null {
    if (project.budgetMonthlyUsd === null || project.budgetMonthlyUsd <= 0) return null;
    return project.costUsd / project.budgetMonthlyUsd;
  }

  budgetPercent(project: ProjectUsage): number | null {
    const ratio = MonitoringComponent.budgetRatio(project);
    return ratio === null ? null : Math.round(ratio * 100);
  }

  /** Taux de réussite d'une ligne, en pourcentage. Zéro run ⇒ rien à afficher. */
  ratePercent(runs: number, succeeded: number): number {
    return runs > 0 ? Math.round((succeeded / runs) * 100) : 0;
  }

  /** Une durée en millisecondes rendue lisible : personne ne lit « 3 600 000 ms ». */
  humanDuration(ms: number): string {
    if (ms <= 0) return '—';
    const seconds = Math.round(ms / 1000);
    if (seconds < 60) return `${seconds} s`;
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes} min ${seconds % 60} s`;
    return `${Math.floor(minutes / 60)} h ${minutes % 60} min`;
  }

  /** Étiquette courte d'un point de la série, pour l'axe. */
  dayLabel(point: DailyPoint): string {
    return point.day.slice(5, 10);
  }
}
