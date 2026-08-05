import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SecretService } from './secret.service';
import { environment } from '../../environments/environment';
import { Secret } from '../core/models';

const URL = `${environment.apiUrl}/api/secrets`;

function secret(id: string, name: string): Secret {
  return {
    id,
    orgId: 'o1',
    projectId: null,
    name,
    scope: 'org',
    lastUsedAt: null,
    lastUsedByRunId: null,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('SecretService', () => {
  let service: SecretService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(SecretService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('lists without an orgId query param — the server scopes by the JWT', async () => {
    const pending = service.listSecrets();

    const req = httpMock.expectOne(URL);
    expect(req.request.params.keys()).toEqual([]);
    req.flush([secret('s1', 'API_KEY')]);

    await pending;
    expect(service.secrets().length).toBe(1);
    expect(service.error()).toBeNull();
  });

  it('surfaces a translation key and an empty list when the load fails', async () => {
    const pending = service.listSecrets();
    httpMock.expectOne(URL).flush(null, { status: 403, statusText: 'Forbidden' });
    await pending;

    expect(service.secrets()).toEqual([]);
    expect(service.error()).toBe('adminSecrets.loadError');
    expect(service.isLoading()).toBe(false);
  });

  it('rotates through PUT and replaces the row in place', async () => {
    const initial = service.listSecrets();
    httpMock.expectOne(URL).flush([secret('s1', 'API_KEY'), secret('s2', 'OTHER')]);
    await initial;

    const pending = service.rotateSecret('s1', 'brand-new-value');
    const req = httpMock.expectOne(`${URL}/s1`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ value: 'brand-new-value' });
    req.flush({ ...secret('s1', 'API_KEY'), updatedAt: '2026-02-02T00:00:00Z' });
    await pending;

    expect(service.secrets().length).toBe(2);
    expect(service.secrets()[0].updatedAt).toBe('2026-02-02T00:00:00Z');
    // Responses never carry the value back.
    expect('value' in service.secrets()[0]).toBe(false);
  });

  it('creates without an orgId in the body', async () => {
    const pending = service.createSecret({ name: 'API_KEY', value: 'v', scope: 'org' });
    const req = httpMock.expectOne(URL);
    expect(req.request.body).toEqual({ name: 'API_KEY', value: 'v', scope: 'org' });
    req.flush(secret('s1', 'API_KEY'));
    await pending;

    expect(service.secrets().length).toBe(1);
  });
});
