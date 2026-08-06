import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AgentService } from './agent.service';
import { environment } from '../../environments/environment';
import { Agent, AgentVersion } from '../core/models';

const URL = `${environment.apiUrl}/api/agents`;

function agent(id: string, name = 'Builder'): Agent {
  return {
    id,
    orgId: 'o1',
    projectId: 'p1',
    name,
    slug: name.toLowerCase(),
    agentType: 'oci',
    imageRef: null,
    manifestYaml: 'name: builder',
    inputsSchema: { type: 'object' } as never,
    outputsSchema: null,
    currentVersionId: 'v1',
    isPublished: true,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

function version(id: string, n: number): AgentVersion {
  return {
    id,
    agentId: 'a1',
    versionNumber: n,
    manifestYaml: 'name: builder',
    imageRef: null,
    inputsSchema: '{}',
    outputsSchema: null,
    digestSha256: 'deadbeef',
    createdAt: '2026-01-01T00:00:00Z',
  };
}

describe('AgentService', () => {
  let service: AgentService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(AgentService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  describe('listAgents', () => {
    it('GETs the bare collection when no project is given', async () => {
      const pending = service.listAgents();
      const req = httpMock.expectOne(URL);
      expect(req.request.method).toBe('GET');
      expect(req.request.params.keys()).toEqual([]);
      req.flush([agent('a1')]);

      await pending;
      expect(service.agentCount()).toBe(1);
      expect(service.error()).toBeNull();
      expect(service.isLoading()).toBe(false);
    });

    it('scopes by projectId as a url-encoded query param', async () => {
      const pending = service.listAgents('p 1/x');
      const req = httpMock.expectOne(`${URL}?projectId=${encodeURIComponent('p 1/x')}`);
      expect(req.request.method).toBe('GET');
      req.flush([]);
      await pending;
      expect(service.agents()).toEqual([]);
    });

    it('sets the error signal on failure', async () => {
      const pending = service.listAgents();
      httpMock.expectOne(URL).flush(null, { status: 500, statusText: 'Server Error' });
      await pending;

      expect(service.error()).toBe('errors.loadAgents');
      expect(service.isLoading()).toBe(false);
    });
  });

  describe('createAgent payload', () => {
    // REGRESSION: no orgId in the body — the server takes the org from the JWT. projectId is a
    // legitimate routing field and must stay; orgId must never come back.
    it('never sends orgId or a client-supplied identity field in the body', async () => {
      const pending = service.createAgent({
        projectId: 'p1',
        name: 'Builder',
        slug: 'builder',
        manifestYaml: 'name: builder',
      });

      const req = httpMock.expectOne(URL);
      expect(req.request.method).toBe('POST');
      const keys = Object.keys(req.request.body as object);
      expect(keys).not.toContain('orgId');
      expect(keys).not.toContain('organizationId');
      expect(keys).not.toContain('createdByUserId');
      expect(keys).not.toContain('userId');
      expect(req.request.body).toEqual({
        projectId: 'p1',
        name: 'Builder',
        slug: 'builder',
        manifestYaml: 'name: builder',
      });

      req.flush(agent('a1'));
      await pending;
      expect(service.agents().length).toBe(1);
    });

    it('propagates a create failure', async () => {
      const pending = service.createAgent({
        projectId: 'p1',
        name: 'Builder',
        slug: 'builder',
        manifestYaml: 'x',
      });
      httpMock.expectOne(URL).flush(null, { status: 400, statusText: 'Bad Request' });

      await expect(pending).rejects.toBeTruthy();
      expect(service.agents()).toEqual([]);
    });
  });

  describe('fetchAgent', () => {
    it('upserts rather than duplicating a known agent', async () => {
      const first = service.fetchAgent('a1');
      httpMock.expectOne(`${URL}/a1`).flush(agent('a1'));
      expect(await first).not.toBeNull();

      const second = service.fetchAgent('a1');
      httpMock.expectOne(`${URL}/a1`).flush(agent('a1', 'Renamed'));
      await second;

      expect(service.agents().length).toBe(1);
      expect(service.agents()[0].name).toBe('Renamed');
    });

    // Renvoyer `null` en cas d'échec laissait le `catch` de l'appelant inerte : la page de détail
    // affichait un corps entièrement vide sur un identifiant inconnu. La méthode lève désormais.
    it('rethrows on failure instead of resolving null', async () => {
      const pending = service.fetchAgent('a9');
      httpMock.expectOne(`${URL}/a9`).flush(null, { status: 404, statusText: 'Not Found' });

      await expect(pending).rejects.toBeTruthy();
      expect(service.error()).toBe('errors.loadAgent');
      expect(service.isLoading()).toBe(false);
    });
  });

  describe('versions', () => {
    it('lists versions and clears the version loading flag', async () => {
      const pending = service.listVersions('a1');
      expect(service.isLoadingVersions()).toBe(true);

      const req = httpMock.expectOne(`${URL}/a1/versions`);
      expect(req.request.method).toBe('GET');
      req.flush([version('v2', 2), version('v1', 1)]);

      expect((await pending).length).toBe(2);
      expect(service.versions().length).toBe(2);
      expect(service.isLoadingVersions()).toBe(false);
    });

    it('rethrows a version-list failure instead of swallowing it', async () => {
      const pending = service.listVersions('a1');
      httpMock.expectOne(`${URL}/a1/versions`).flush(null, { status: 500, statusText: 'Err' });

      await expect(pending).rejects.toBeTruthy();
      expect(service.error()).toBe('errors.loadAgentVersions');
      expect(service.isLoadingVersions()).toBe(false);
    });

    it('publishes only the manifest and prepends the new version', async () => {
      const seed = service.listVersions('a1');
      httpMock.expectOne(`${URL}/a1/versions`).flush([version('v1', 1)]);
      await seed;

      const pending = service.publishVersion('a1', 'name: builder\nversion: 2');
      const req = httpMock.expectOne(`${URL}/a1/versions`);
      expect(req.request.method).toBe('POST');
      expect(req.request.body).toEqual({ manifestYaml: 'name: builder\nversion: 2' });
      expect(Object.keys(req.request.body as object)).not.toContain('orgId');
      req.flush(version('v2', 2));
      await pending;

      expect(service.versions().map((v) => v.id)).toEqual(['v2', 'v1']);
    });
  });
});
