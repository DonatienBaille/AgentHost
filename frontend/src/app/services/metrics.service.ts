import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { AgentUsage, DailyPoint, MetricsOverview, ProjectUsage } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/metrics`;

/**
 * Les quatre agrégats du tableau de bord, chargés ensemble.
 *
 * Ensemble et non un par un : ils décrivent la même fenêtre temporelle, et les charger séparément
 * ferait afficher côte à côte des chiffres calculés à des instants différents — un total qui ne
 * correspond pas à la somme de sa ventilation est le genre d'incohérence qui fait douter de tout
 * l'écran.
 */
@Injectable({ providedIn: 'root' })
export class MetricsService {
  private readonly http = inject(HttpClient);

  readonly overview = signal<MetricsOverview | null>(null);
  readonly byAgent = signal<AgentUsage[]>([]);
  readonly byProject = signal<ProjectUsage[]>([]);
  readonly daily = signal<DailyPoint[]>([]);

  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  /** Fenêtre courante, en jours. Le serveur la plafonne à 90 ; on lui laisse ce dernier mot. */
  readonly windowDays = signal(30);

  readonly successRate = computed(() => {
    const o = this.overview();
    return o && o.totalRuns > 0 ? (o.succeededRuns / o.totalRuns) * 100 : 0;
  });

  async load(days = this.windowDays()): Promise<void> {
    this.isLoading.set(true);
    try {
      const query = `?days=${days}`;
      const [overview, byAgent, byProject, daily] = await Promise.all([
        firstValueFrom(this.http.get<MetricsOverview>(`${BASE_URL}/overview${query}`)),
        firstValueFrom(this.http.get<AgentUsage[]>(`${BASE_URL}/by-agent${query}`)),
        firstValueFrom(this.http.get<ProjectUsage[]>(`${BASE_URL}/by-project${query}`)),
        firstValueFrom(this.http.get<DailyPoint[]>(`${BASE_URL}/daily${query}`)),
      ]);

      this.overview.set(overview);
      this.byAgent.set(byAgent ?? []);
      this.byProject.set(byProject ?? []);
      this.daily.set(daily ?? []);
      // La fenêtre affichée est celle que le serveur a retenue, pas celle demandée : il la
      // plafonne, et montrer « 9999 jours » au-dessus de 90 jours de données serait un mensonge.
      this.windowDays.set(overview.windowDays);
      this.error.set(null);
    } catch {
      this.error.set('errors.loadMetrics');
    } finally {
      this.isLoading.set(false);
    }
  }
}
