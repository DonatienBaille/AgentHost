import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AUDIT_PAGE_SIZE, AuditService } from './audit.service';
import { environment } from '../../environments/environment';
import { auditEntryView, auditFacets, auditPage } from '../core/testing/fixtures';

const ORG = 'org-1';
const BASE = `${environment.apiUrl}/api/organizations/${ORG}/audit-log`;

/**
 * Le service de consultation du journal d'audit.
 *
 * Ce qui mérite d'être épinglé ici tient en deux points. Les filtres partent au serveur — filtrer
 * dans le navigateur supposerait d'avoir chargé le journal entier, ce qui n'a de sens que sur un
 * journal qu'on n'a pas besoin de filtrer. Et les bornes de période sont **inclusives des deux
 * côtés dans l'IHM** alors que l'API exclut la sienne : la conversion se fait ici, une fois, et
 * c'est exactement le genre de décalage d'un jour qui passe inaperçu jusqu'à ce qu'un auditeur
 * constate qu'il manque une journée.
 */
describe('AuditService', () => {
  let service: AuditService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AuditService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  /** Renvoie la requête en attente sur le journal, en ayant vérifié qu'il n'y en a qu'une. */
  function expectSearch() {
    return httpMock.expectOne((req) => req.url === BASE);
  }

  describe('search', () => {
    it('asks for the first page with the clamped page size and no filter', async () => {
      const pending = service.search(ORG);
      const req = expectSearch();

      expect(req.request.params.get('skip')).toBe('0');
      expect(req.request.params.get('take')).toBe(String(AUDIT_PAGE_SIZE));
      // Un filtre non renseigné ne doit pas partir : `?action=` demanderait les entrées dont
      // l'action est la chaîne vide, c'est-à-dire aucune.
      expect(req.request.params.has('action')).toBe(false);

      req.flush(auditPage({ items: [auditEntryView({ id: 'a1' })], total: 1 }));
      await pending;

      expect(service.entries().length).toBe(1);
      expect(service.total()).toBe(1);
      expect(service.error()).toBeNull();
      expect(service.isLoading()).toBe(false);
    });

    it('carries every filled filter and drops the empty ones', async () => {
      const pending = service.search(ORG, {
        action: 'user.updated',
        actorUserId: 'u9',
        resourceType: 'user',
        resourceId: '',
      });
      const req = expectSearch();

      expect(req.request.params.get('action')).toBe('user.updated');
      expect(req.request.params.get('actorUserId')).toBe('u9');
      expect(req.request.params.get('resourceType')).toBe('user');
      expect(req.request.params.has('resourceId')).toBe(false);

      req.flush(auditPage({ items: [] , total: 0 }));
      await pending;
    });

    it('turns an inclusive day range into the exclusive upper bound the API expects', async () => {
      const pending = service.search(ORG, { from: '2031-03-03', to: '2031-03-05' });
      const req = expectSearch();

      expect(req.request.params.get('from')).toBe('2031-03-03T00:00:00Z');
      // « du 3 au 5 » contient le 5 : la borne partante est donc le 6 à minuit UTC. Envoyer le 5
      // ferait disparaître toute la dernière journée demandée, sans rien signaler.
      expect(req.request.params.get('to')).toBe('2031-03-06T00:00:00Z');

      req.flush(auditPage({ items: [], total: 0 }));
      await pending;
    });

    it('rolls the exclusive bound over a month and a year end', async () => {
      // Le débordement est délégué à `Date.UTC` plutôt qu'écrit à la main : ajouter un jour au 31
      // décembre n'a pas de cas particulier, et l'année bissextile n'en a pas non plus.
      const cases: [string, string][] = [
        ['2031-12-31', '2032-01-01'],
        ['2031-04-30', '2031-05-01'],
        ['2032-02-28', '2032-02-29'],
      ];

      for (const [day, expected] of cases) {
        const pending = service.search(ORG, { to: day });
        const req = expectSearch();
        expect(req.request.params.get('to')).toBe(`${expected}T00:00:00Z`);
        req.flush(auditPage({ items: [], total: 0 }));
        await pending;
      }
    });

    it('ignores a malformed date instead of sending it to the server', async () => {
      const pending = service.search(ORG, { from: 'hier', to: '' });
      const req = expectSearch();

      expect(req.request.params.has('from')).toBe(false);
      expect(req.request.params.has('to')).toBe(false);

      req.flush(auditPage({ items: [], total: 0 }));
      await pending;
    });

    it('adopts the paging the server actually applied', async () => {
      const pending = service.search(ORG, {}, 100);
      // Le serveur borne `take` : c'est sa valeur qui doit gouverner le calcul de la page
      // suivante, sinon la pagination sauterait des entrées.
      expectSearch().flush(auditPage({ items: [auditEntryView()], total: 300, skip: 100, take: 50 }));
      await pending;

      expect(service.skip()).toBe(100);
      expect(service.take()).toBe(50);
      expect(service.hasMore()).toBe(true);
    });

    it('knows it has reached the end of the log', async () => {
      const pending = service.search(ORG, {}, 40);
      expectSearch().flush(auditPage({ items: [auditEntryView(), auditEntryView({ id: 'a2' })], total: 42, skip: 40, take: 50 }));
      await pending;

      expect(service.hasMore()).toBe(false);
    });

    it('reports a failure and leaves nothing half-loaded', async () => {
      const pending = service.search(ORG);
      expectSearch().flush(null, { status: 500, statusText: 'Err' });
      await pending;

      expect(service.error()).toBe('errors.loadAuditLog');
      expect(service.entries()).toEqual([]);
      expect(service.isLoading()).toBe(false);
    });

    it('treats a null body as an empty page rather than crashing the page', async () => {
      const pending = service.search(ORG);
      expectSearch().flush(null);
      await pending;

      expect(service.entries()).toEqual([]);
      expect(service.total()).toBe(0);
    });
  });

  describe('facets', () => {
    it('loads the values present in the log', async () => {
      const pending = service.loadFacets(ORG);
      httpMock.expectOne(`${BASE}/facets`).flush(auditFacets());
      await pending;

      expect(service.facets()?.actions.length).toBe(2);
      expect(service.facets()?.actors[0].email).toBe('alice@example.com');
    });

    it('fails silently: the log stays readable without its filter lists', async () => {
      const pending = service.loadFacets(ORG);
      httpMock.expectOne(`${BASE}/facets`).flush(null, { status: 500, statusText: 'Err' });
      await pending;

      expect(service.facets()).toBeNull();
      // Surtout pas d'erreur affichée : elle laisserait croire que le journal n'a pas pu être lu,
      // alors qu'il est à l'écran.
      expect(service.error()).toBeNull();
    });
  });
});
