import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { MetricsService } from './metrics.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/api/metrics`;

/**
 * Le service qui alimente le tableau de bord.
 *
 * Deux propriétés méritent d'être épinglées. Les quatre agrégats sont chargés **ensemble** : ils
 * décrivent la même fenêtre, et les charger séparément ferait afficher côte à côte des chiffres
 * calculés à des instants différents. Et la fenêtre affichée est celle que le **serveur** a retenue,
 * pas celle demandée — il la plafonne, et annoncer « 9999 jours » au-dessus de 90 jours de données
 * serait un mensonge.
 */
describe('MetricsService', () => {
  let service: MetricsService;
  let httpMock: HttpTestingController;

  const overview = {
    windowDays: 30, totalRuns: 4, succeededRuns: 3, failedRuns: 1, infraErrorRuns: 0,
    budgetExceededRuns: 0, inFlightRuns: 0, costUsd: 2, averageDurationMs: 100,
    oldestPendingApprovalSeconds: 0,
  };

  function flushAll(days: number, overrides: Partial<typeof overview> = {}): void {
    httpMock.expectOne(`${BASE}/overview?days=${days}`).flush({ ...overview, ...overrides });
    httpMock.expectOne(`${BASE}/by-agent?days=${days}`).flush([]);
    httpMock.expectOne(`${BASE}/by-project?days=${days}`).flush([]);
    httpMock.expectOne(`${BASE}/daily?days=${days}`).flush([]);
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(MetricsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('loads the four aggregates in one go, all on the same window', async () => {
    const pending = service.load(30);
    flushAll(30);
    await pending;

    expect(service.overview()?.totalRuns).toBe(4);
    expect(service.byAgent()).toEqual([]);
    expect(service.error()).toBeNull();
    expect(service.isLoading()).toBe(false);
  });

  it('carries the requested window into every call', async () => {
    const pending = service.load(7);
    flushAll(7);
    await pending;
  });

  it('adopts the window the server actually applied, not the one asked for', async () => {
    const pending = service.load(9999);
    // Le serveur plafonne à 90 : c'est ce chiffre-là qui doit s'afficher au-dessus des données.
    flushAll(9999, { windowDays: 90 });
    await pending;

    expect(service.windowDays()).toBe(90);
  });

  it('computes the success rate, and does not divide by zero', async () => {
    const first = service.load(30);
    flushAll(30, { totalRuns: 4, succeededRuns: 3 });
    await first;
    expect(service.successRate()).toBe(75);

    const second = service.load(30);
    flushAll(30, { totalRuns: 0, succeededRuns: 0 });
    await second;
    expect(service.successRate()).toBe(0);
  });

  it('reports a failure once rather than leaving a half-loaded screen', async () => {
    const pending = service.load(30);
    httpMock.expectOne(`${BASE}/overview?days=30`).flush(null, { status: 500, statusText: 'Err' });
    httpMock.expectOne(`${BASE}/by-agent?days=30`).flush([]);
    httpMock.expectOne(`${BASE}/by-project?days=30`).flush([]);
    httpMock.expectOne(`${BASE}/daily?days=30`).flush([]);
    await pending;

    expect(service.error()).toBe('errors.loadMetrics');
    expect(service.isLoading()).toBe(false);
    // Rien n'est posé : un tableau à moitié rempli serait pris pour la réalité.
    expect(service.overview()).toBeNull();
  });

  it('treats a null collection as empty rather than crashing the page', async () => {
    const pending = service.load(30);
    httpMock.expectOne(`${BASE}/overview?days=30`).flush(overview);
    httpMock.expectOne(`${BASE}/by-agent?days=30`).flush(null);
    httpMock.expectOne(`${BASE}/by-project?days=30`).flush(null);
    httpMock.expectOne(`${BASE}/daily?days=30`).flush(null);
    await pending;

    expect(service.byAgent()).toEqual([]);
    expect(service.byProject()).toEqual([]);
    expect(service.daily()).toEqual([]);
  });
});
