import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TriggerService } from './trigger.service';
import { environment } from '../../environments/environment';
import { trigger } from '../core/testing/fixtures';

const PROJECT = 'p1';
const LIST_URL = `${environment.apiUrl}/api/projects/${PROJECT}/triggers`;

/**
 * Le service des déclencheurs.
 *
 * Deux propriétés méritent d'être épinglées ici. Le <b>secret</b> rendu à la création traverse la
 * méthode sans jamais être mémorisé : le garder dans un signal le laisserait en mémoire pour toute
 * la session et le ferait réapparaître à la navigation suivante, ce qui contredirait la promesse
 * « affiché une seule fois » que l'écran fait à l'utilisateur. Et la <b>création laisse remonter
 * son échec</b> au lieu de poser un signal d'erreur : l'appelant a un formulaire ouvert et un
 * message précis à afficher — une expression cron refusée n'est pas une panne de chargement.
 */
describe('TriggerService', () => {
  let service: TriggerService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(TriggerService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    TestBed.resetTestingModule();
  });

  describe('list', () => {
    it('loads the triggers of a project', async () => {
      const pending = service.list(PROJECT);
      httpMock.expectOne(LIST_URL).flush([trigger({ id: 't1' })]);
      await pending;

      expect(service.triggers().length).toBe(1);
      expect(service.error()).toBeNull();
      expect(service.isLoading()).toBe(false);
    });

    it('reports a failure without leaving a stale list', async () => {
      const pending = service.list(PROJECT);
      httpMock.expectOne(LIST_URL).flush(null, { status: 500, statusText: 'Err' });
      await pending;

      expect(service.error()).toBe('errors.loadTriggers');
      expect(service.triggers()).toEqual([]);
    });

    it('treats a null body as an empty list rather than crashing the page', async () => {
      const pending = service.list(PROJECT);
      httpMock.expectOne(LIST_URL).flush(null);
      await pending;

      expect(service.triggers()).toEqual([]);
    });
  });

  describe('create', () => {
    it('returns the secret to the caller and keeps none of it', async () => {
      const pending = service.create(PROJECT, { agentId: 'a1', name: 'Push', type: 'webhook' });
      httpMock.expectOne(LIST_URL).flush({ trigger: trigger({ id: 't9' }), secret: 'sh-secret' });
      const created = await pending;

      expect(created.secret).toBe('sh-secret');
      // Le déclencheur entre dans la liste, le secret nulle part : rien dans l'état du service ne
      // doit permettre de le retrouver après coup.
      expect(service.triggers()[0].id).toBe('t9');
      expect(JSON.stringify(service.triggers())).not.toContain('sh-secret');
    });

    it('prepends rather than reloading: the server just returned the object', async () => {
      const first = service.list(PROJECT);
      httpMock.expectOne(LIST_URL).flush([trigger({ id: 'old' })]);
      await first;

      const pending = service.create(PROJECT, { agentId: 'a1', name: 'Push', type: 'webhook' });
      httpMock.expectOne(LIST_URL).flush({ trigger: trigger({ id: 'new' }), secret: 's' });
      await pending;

      expect(service.triggers().map((t) => t.id)).toEqual(['new', 'old']);
    });

    it('lets the failure through so the form can show the server reason', async () => {
      const pending = service.create(PROJECT, { agentId: 'a1', name: 'X', type: 'cron' });
      httpMock
        .expectOne(LIST_URL)
        .flush({ error: "Unrecognized value 'lundi'" }, { status: 400, statusText: 'Bad Request' });

      // Rejetée, pas avalée : « une erreur est survenue » enlèverait le seul moyen de corriger.
      await expect(pending).rejects.toBeDefined();
      expect(service.error()).toBeNull();
    });
  });

  describe('update and delete', () => {
    it('replaces the updated trigger in place', async () => {
      const first = service.list(PROJECT);
      httpMock.expectOne(LIST_URL).flush([trigger({ id: 't1', isActive: true })]);
      await first;

      const pending = service.update('t1', { isActive: false });
      const req = httpMock.expectOne(`${environment.apiUrl}/api/triggers/t1`);
      expect(req.request.method).toBe('PATCH');
      req.flush(trigger({ id: 't1', isActive: false }));
      await pending;

      expect(service.triggers()[0].isActive).toBe(false);
      expect(service.triggers().length).toBe(1);
    });

    it('drops the removed trigger from the list', async () => {
      const first = service.list(PROJECT);
      httpMock.expectOne(LIST_URL).flush([trigger({ id: 't1' }), trigger({ id: 't2' })]);
      await first;

      const pending = service.remove('t1');
      httpMock.expectOne(`${environment.apiUrl}/api/triggers/t1`).flush(null);
      await pending;

      expect(service.triggers().map((t) => t.id)).toEqual(['t2']);
    });
  });
});
