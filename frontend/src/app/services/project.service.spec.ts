import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ProjectService } from './project.service';
import { environment } from '../../environments/environment';
import { Project } from '../core/models';

const URL = `${environment.apiUrl}/api/projects`;

function project(id: string, name = 'Alpha'): Project {
  return {
    id,
    orgId: 'o1',
    name,
    slug: name.toLowerCase(),
    description: null,
    budgetMonthlyUsd: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('ProjectService', () => {
  let service: ProjectService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(ProjectService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  describe('listProjects', () => {
    it('GETs the collection with no query params and fills the signals', async () => {
      const pending = service.listProjects();
      expect(service.isLoading()).toBe(true);

      const req = httpMock.expectOne(URL);
      expect(req.request.method).toBe('GET');
      expect(req.request.params.keys()).toEqual([]);
      req.flush([project('p1'), project('p2', 'Beta')]);

      await pending;
      expect(service.projects().length).toBe(2);
      expect(service.projectCount()).toBe(2);
      expect(service.error()).toBeNull();
      expect(service.isLoading()).toBe(false);
    });

    it('sets error and clears loading when the request fails', async () => {
      const pending = service.listProjects();
      httpMock.expectOne(URL).flush(null, { status: 500, statusText: 'Server Error' });
      await pending;

      expect(service.error()).toBe('Failed to load projects');
      expect(service.isLoading()).toBe(false);
    });

    it('treats a null body as an empty list', async () => {
      const pending = service.listProjects();
      httpMock.expectOne(URL).flush(null);
      await pending;

      expect(service.projects()).toEqual([]);
      expect(service.error()).toBeNull();
    });
  });

  describe('createProject payload', () => {
    // REGRESSION: the server derives the owning org from the JWT. If orgId (or any other
    // client-supplied identity field) is reintroduced into the body, this must fail.
    it('never sends orgId or a client-supplied identity field in the body', async () => {
      const pending = service.createProject({ name: 'Alpha', slug: 'alpha' });

      const req = httpMock.expectOne(URL);
      expect(req.request.method).toBe('POST');
      const body = req.request.body as Record<string, unknown>;
      const keys = Object.keys(body);
      expect(keys).not.toContain('orgId');
      expect(keys).not.toContain('organizationId');
      expect(keys).not.toContain('createdByUserId');
      expect(keys).not.toContain('userId');
      expect(body).toEqual({ name: 'Alpha', slug: 'alpha' });

      req.flush(project('p1'));
      await pending;
      expect(service.projects().length).toBe(1);
    });

    it('forwards only the optional fields the caller actually supplied', async () => {
      const pending = service.createProject({
        name: 'Alpha',
        slug: 'alpha',
        description: 'desc',
        budgetMonthlyUsd: 42,
      });

      const req = httpMock.expectOne(URL);
      expect(Object.keys(req.request.body as object).sort()).toEqual([
        'budgetMonthlyUsd',
        'description',
        'name',
        'slug',
      ]);
      req.flush(project('p1'));
      await pending;
    });

    it('propagates a create failure to the caller', async () => {
      const pending = service.createProject({ name: 'Alpha', slug: 'alpha' });
      httpMock.expectOne(URL).flush(null, { status: 409, statusText: 'Conflict' });

      await expect(pending).rejects.toBeTruthy();
      expect(service.projects()).toEqual([]);
    });
  });

  describe('fetchProject', () => {
    it('appends an unknown project and replaces a known one in place', async () => {
      const first = service.fetchProject('p1');
      httpMock.expectOne(`${URL}/p1`).flush(project('p1'));
      await first;
      expect(service.projects().length).toBe(1);

      const second = service.fetchProject('p1');
      httpMock.expectOne(`${URL}/p1`).flush(project('p1', 'Renamed'));
      await second;

      expect(service.projects().length).toBe(1);
      expect(service.projects()[0].name).toBe('Renamed');
    });

    it('reports the failing id in the error signal', async () => {
      const pending = service.fetchProject('p9');
      httpMock.expectOne(`${URL}/p9`).flush(null, { status: 404, statusText: 'Not Found' });
      await pending;

      expect(service.error()).toBe('Failed to load project p9');
      expect(service.isLoading()).toBe(false);
    });
  });

  it('fetchMemory GETs the memory sub-resource and caches it', async () => {
    const memory = { projectId: 'p1', content: 'notes' } as never;
    const pending = service.fetchMemory('p1');
    const req = httpMock.expectOne(`${URL}/p1/memory`);
    expect(req.request.method).toBe('GET');
    req.flush(memory);

    await pending;
    expect(service.currentMemory()).toEqual(memory);
  });

  it('currentProject resolves the selected id against the loaded list', async () => {
    const pending = service.listProjects();
    httpMock.expectOne(URL).flush([project('p1'), project('p2', 'Beta')]);
    await pending;

    expect(service.currentProject()).toBeNull();
    service.selectProject('p2');
    expect(service.currentProject()?.name).toBe('Beta');
    service.selectProject('nope');
    expect(service.currentProject()).toBeNull();
  });
});
