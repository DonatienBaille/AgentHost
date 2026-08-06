import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { OrganizationService } from './organization.service';
import { environment } from '../../environments/environment';
import { Organization } from '../core/models';

const URL = `${environment.apiUrl}/api/organizations`;

function org(id: string, name = 'Acme'): Organization {
  return {
    id,
    name,
    slug: name.toLowerCase(),
    plan: 'free',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('OrganizationService', () => {
  let service: OrganizationService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(OrganizationService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('lists with no query params', async () => {
    const pending = service.listOrganizations();
    expect(service.isLoading()).toBe(true);

    const req = httpMock.expectOne(URL);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.keys()).toEqual([]);
    req.flush([org('o1')]);

    await pending;
    expect(service.organizations().length).toBe(1);
    expect(service.error()).toBeNull();
    expect(service.isLoading()).toBe(false);
  });

  it('treats a null body as an empty list', async () => {
    const pending = service.listOrganizations();
    httpMock.expectOne(URL).flush(null);
    await pending;
    expect(service.organizations()).toEqual([]);
  });

  it('sets the error signal and stops loading on failure', async () => {
    const pending = service.listOrganizations();
    httpMock.expectOne(URL).flush(null, { status: 500, statusText: 'Err' });
    await pending;

    expect(service.error()).toBe('Failed to load organizations');
    expect(service.isLoading()).toBe(false);
  });

  it('creates with exactly the supplied fields and appends the result', async () => {
    const pending = service.createOrganization({ name: 'Acme', slug: 'acme' });

    const req = httpMock.expectOne(URL);
    expect(req.request.method).toBe('POST');
    const keys = Object.keys(req.request.body as object);
    expect(keys).not.toContain('createdByUserId');
    expect(keys).not.toContain('userId');
    expect(req.request.body).toEqual({ name: 'Acme', slug: 'acme' });
    req.flush(org('o1'));

    await pending;
    expect(service.organizations().map((o) => o.id)).toEqual(['o1']);
  });

  it('propagates a create failure to the caller', async () => {
    const pending = service.createOrganization({ name: 'Acme', slug: 'acme' });
    httpMock.expectOne(URL).flush(null, { status: 409, statusText: 'Conflict' });

    await expect(pending).rejects.toBeTruthy();
    expect(service.organizations()).toEqual([]);
  });
});
