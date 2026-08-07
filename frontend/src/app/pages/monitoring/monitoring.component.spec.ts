import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { MonitoringComponent } from './monitoring.component';
import { MetricsService } from '../../services/metrics.service';
import { AgentUsage, DailyPoint, MetricsOverview, ProjectUsage } from '../../core/models';

/**
 * Le tableau de bord de supervision (lot 3).
 *
 * L'essentiel des tests porte sur les **alertes**, parce que c'est la seule partie de l'écran qui
 * porte un jugement. Un chiffre affiché de travers se remarque ; une alerte qui ne se déclenche pas
 * ne se remarque jamais — et une alerte qui se déclenche à tort finit par être ignorée, ce qui
 * revient au même. Les seuils sont donc épinglés des deux côtés : juste en dessous et juste au-dessus.
 */
function overview(o: Partial<MetricsOverview> = {}): MetricsOverview {
  return {
    windowDays: 30,
    totalRuns: 10,
    succeededRuns: 8,
    failedRuns: 2,
    infraErrorRuns: 1,
    budgetExceededRuns: 0,
    inFlightRuns: 1,
    costUsd: 12.5,
    averageDurationMs: 90_000,
    oldestPendingApprovalSeconds: 0,
    ...o,
  };
}

function agentUsage(o: Partial<AgentUsage> = {}): AgentUsage {
  return { agentId: 'ag1', name: 'Builder', runs: 10, succeeded: 9, costUsd: 4, averageDurationMs: 1000, ...o };
}

function projectUsage(o: Partial<ProjectUsage> = {}): ProjectUsage {
  return { projectId: 'p1', name: 'Alpha', runs: 5, succeeded: 5, costUsd: 10, budgetMonthlyUsd: 100, ...o };
}

class MetricsServiceStub {
  readonly overview = signal<MetricsOverview | null>(null);
  readonly byAgent = signal<AgentUsage[]>([]);
  readonly byProject = signal<ProjectUsage[]>([]);
  readonly daily = signal<DailyPoint[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly windowDays = signal(30);
  readonly successRate = signal(0);
  load = vi.fn(async (_days?: number) => {});
}

describe('MonitoringComponent', () => {
  let fixture: ComponentFixture<MonitoringComponent>;
  let component: MonitoringComponent;
  let metrics: MetricsServiceStub;

  beforeEach(() => {
    metrics = new MetricsServiceStub();
    TestBed.configureTestingModule({
      imports: [MonitoringComponent],
      providers: [
        provideRouter([]),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: MetricsService, useValue: metrics },
      ],
    });
    fixture = TestBed.createComponent(MonitoringComponent);
    component = fixture.componentInstance;
  });

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function all(testId: string): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`));
  }

  describe('chargement', () => {
    it('asks for the default window on init', () => {
      fixture.detectChanges();
      expect(metrics.load).toHaveBeenCalledExactlyOnceWith(30);
    });

    it('reloads when another window is chosen', async () => {
      fixture.detectChanges();
      await component.selectWindow(7);

      expect(metrics.load).toHaveBeenLastCalledWith(7);
      expect(component.selectedWindow()).toBe(7);
    });

    it('renders the service error', () => {
      metrics.error.set('errors.loadMetrics');
      fixture.detectChanges();

      expect(el('monitoring-error')?.textContent).toContain('errors.loadMetrics');
    });

    it('shows the empty state of each table when there is nothing to show', () => {
      fixture.detectChanges();

      expect(el('agents-empty')).not.toBeNull();
      expect(el('projects-empty')).not.toBeNull();
    });
  });

  describe('chiffres de tête', () => {
    it('renders the headline numbers', () => {
      metrics.overview.set(overview({ totalRuns: 42, costUsd: 7.5, inFlightRuns: 3 }));
      metrics.successRate.set(80);
      fixture.detectChanges();

      expect(el('stat-runs')?.textContent?.trim()).toBe('42');
      expect(el('stat-success-rate')?.textContent?.trim()).toBe('80%');
      expect(el('stat-cost')?.textContent).toContain('7.50');
      expect(el('stat-in-flight')?.textContent?.trim()).toBe('3');
    });

    it('breaks the failures down instead of showing one opaque total', () => {
      metrics.overview.set(overview({ failedRuns: 5, infraErrorRuns: 3, budgetExceededRuns: 1 }));
      fixture.detectChanges();

      // Un taux de réussite seul ne dit pas où intervenir : un agent qui échoue et une
      // infrastructure qui tombe ne demandent pas le même geste.
      expect(el('stat-failed')?.textContent?.trim()).toBe('5');
      expect(el('stat-infra')?.textContent?.trim()).toBe('3');
      expect(el('stat-budget-exceeded')?.textContent?.trim()).toBe('1');
    });

    it('renders a duration a human can read', () => {
      expect(component.humanDuration(0)).toBe('—');
      expect(component.humanDuration(45_000)).toBe('45 s');
      expect(component.humanDuration(90_000)).toBe('1 min 30 s');
      expect(component.humanDuration(3_930_000)).toBe('1 h 5 min');
    });
  });

  describe('alertes de budget', () => {
    it('says nothing while the budget is comfortable', () => {
      metrics.byProject.set([projectUsage({ costUsd: 50, budgetMonthlyUsd: 100 })]);
      fixture.detectChanges();

      expect(component.alerts()).toEqual([]);
      expect(el('alert-budget-near')).toBeNull();
    });

    it('warns from 80% of the monthly budget', () => {
      metrics.byProject.set([projectUsage({ name: 'Alpha', costUsd: 80, budgetMonthlyUsd: 100 })]);
      fixture.detectChanges();

      const alert = component.alerts()[0];
      expect(alert.key).toBe('monitoring.alerts.budgetNear');
      expect(alert.level).toBe('warning');
      expect(alert.params).toEqual({ project: 'Alpha', percent: 80 });
      expect(el('alert-budget-near')).not.toBeNull();
    });

    it('stays quiet just below the threshold', () => {
      // Le seuil épinglé des deux côtés : une alerte qui part trop tôt finit ignorée.
      metrics.byProject.set([projectUsage({ costUsd: 79, budgetMonthlyUsd: 100 })]);
      fixture.detectChanges();

      expect(component.alerts()).toEqual([]);
    });

    it('escalates to danger once the budget is spent', () => {
      metrics.byProject.set([projectUsage({ costUsd: 120, budgetMonthlyUsd: 100 })]);
      fixture.detectChanges();

      const alert = component.alerts()[0];
      expect(alert.key).toBe('monitoring.alerts.budgetExceeded');
      expect(alert.level).toBe('danger');
      expect(el('alert-budget-exceeded')).not.toBeNull();
    });

    it('says nothing about a project with no budget at all', () => {
      // Sans plafond il n'y a rien à dépasser ; alerter reviendrait à inventer une règle.
      metrics.byProject.set([projectUsage({ costUsd: 9999, budgetMonthlyUsd: null })]);
      fixture.detectChanges();

      expect(component.alerts()).toEqual([]);
      expect(el('budget-none')).not.toBeNull();
      expect(el('budget-percent')).toBeNull();
    });

    it('treats a zero budget as no budget rather than dividing by it', () => {
      metrics.byProject.set([projectUsage({ costUsd: 5, budgetMonthlyUsd: 0 })]);
      fixture.detectChanges();

      expect(component.alerts()).toEqual([]);
      expect(component.budgetPercent(projectUsage({ budgetMonthlyUsd: 0 }))).toBeNull();
    });
  });

  describe("alertes de taux d'échec", () => {
    it('flags an agent failing more than half its runs', () => {
      metrics.byAgent.set([agentUsage({ name: 'Flaky', runs: 10, succeeded: 4 })]);
      fixture.detectChanges();

      const alert = component.alerts()[0];
      expect(alert.key).toBe('monitoring.alerts.failureRate');
      expect(alert.params).toEqual({ agent: 'Flaky', percent: 60 });
      expect(el('alert-failure-rate')).not.toBeNull();
    });

    it('stays quiet at exactly half — the threshold is "more than"', () => {
      metrics.byAgent.set([agentUsage({ runs: 10, succeeded: 5 })]);
      fixture.detectChanges();

      expect(component.alerts()).toEqual([]);
    });

    it('ignores an agent with too few runs to judge', () => {
      // Un agent lancé deux fois et qui a échoué deux fois n'est pas « à 100 % d'échec » : il n'y
      // a rien à conclure, et le dire quand même ferait du bruit à chaque nouvel agent.
      metrics.byAgent.set([agentUsage({ runs: 2, succeeded: 0 })]);
      fixture.detectChanges();

      expect(component.alerts()).toEqual([]);
    });
  });

  describe("alerte d'approbation oubliée", () => {
    it('flags an approval that has been waiting more than a day', () => {
      metrics.overview.set(overview({ oldestPendingApprovalSeconds: 30 * 3600 }));
      fixture.detectChanges();

      const alert = component.alerts()[0];
      expect(alert.key).toBe('monitoring.alerts.approvalStale');
      expect(alert.params).toEqual({ hours: 30 });
      expect(el('alert-approval-stale')).not.toBeNull();
    });

    it('stays quiet for an approval raised this morning', () => {
      metrics.overview.set(overview({ oldestPendingApprovalSeconds: 4 * 3600 }));
      fixture.detectChanges();

      expect(component.alerts()).toEqual([]);
    });
  });

  describe('ordonnancement des alertes', () => {
    it('puts the dangers first — nobody reads the third line of an alert banner', () => {
      metrics.byProject.set([projectUsage({ name: 'Warn', costUsd: 85, budgetMonthlyUsd: 100 })]);
      metrics.byAgent.set([agentUsage({ name: 'Flaky', runs: 10, succeeded: 1 })]);
      metrics.overview.set(overview({ oldestPendingApprovalSeconds: 48 * 3600 }));
      fixture.detectChanges();

      expect(component.alerts().map((a) => a.level)).toEqual(['danger', 'warning', 'warning']);
    });
  });

  describe('série quotidienne', () => {
    function point(day: string, runs: number): DailyPoint {
      return { day, runs, succeeded: runs, costUsd: 0 };
    }

    it('scales the bars against the tallest day, not against a fixed maximum', () => {
      metrics.daily.set([point('2026-01-01', 10), point('2026-01-02', 5)]);
      fixture.detectChanges();

      const bars = component.chartBars();
      expect(bars[0].heightPercent).toBe(100);
      expect(bars[1].heightPercent).toBe(50);
      expect(all('chart-bar')).toHaveLength(2);
    });

    it('keeps an empty day visible so it differs from missing data', () => {
      metrics.daily.set([point('2026-01-01', 10), point('2026-01-02', 0)]);
      fixture.detectChanges();

      expect(component.chartBars()[1].heightPercent).toBe(1);
    });

    it('survives a series where nothing ran, without dividing by zero', () => {
      metrics.daily.set([point('2026-01-01', 0), point('2026-01-02', 0)]);
      fixture.detectChanges();

      expect(component.chartBars().every((b) => Number.isFinite(b.heightPercent))).toBe(true);
    });
  });

  describe('tableaux', () => {
    it('renders one row per agent, linking to its page', () => {
      metrics.byAgent.set([
        agentUsage({ agentId: 'ag1', name: 'Builder', runs: 10, succeeded: 8 }),
        agentUsage({ agentId: 'ag2', name: 'Tester', runs: 4, succeeded: 4 }),
      ]);
      fixture.detectChanges();

      const rows = all('agent-row');
      expect(rows).toHaveLength(2);
      expect(rows[0].querySelector('a')?.getAttribute('href')).toBe('/agents/ag1');
      expect(rows[0].textContent).toContain('80%');
      expect(rows[1].textContent).toContain('100%');
    });

    it('renders the budget share per project, colouring what is over', () => {
      metrics.byProject.set([projectUsage({ costUsd: 95, budgetMonthlyUsd: 100 })]);
      fixture.detectChanges();

      expect(el('budget-percent')?.textContent?.trim()).toBe('95%');
      expect(el('budget-percent')?.classList.contains('text-amber-400')).toBe(true);
    });

    it('shows 0% rather than NaN for a row with no runs', () => {
      expect(component.ratePercent(0, 0)).toBe(0);
    });
  });
});
