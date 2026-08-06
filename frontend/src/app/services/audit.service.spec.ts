import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { AuditService } from './audit.service';
import { environment } from '../../environments/environment';
import { AuditLogEntry } from '../core/models';

const url = (orgId: string, skip: number, take: number) =>
  `${environment.apiUrl}/api/organizations/${orgId}/audit-log?skip=${skip}&take=${take}`;

function entry(id: string): AuditLogEntry {
  return {
    id,
    orgId: 'o1',
    action: 'run.created',
    actorUserId: 'u1',
    resourceType: 'run',
    resourceId: 'r1',
    changes: null,
    details: null,
    createdAt: '2026-01-01T00:00:00Z',
  };
}

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

  afterEach(() => httpMock.verify());

  it('defaults to skip=0 and take=100 on the org sub-resource', async () => {
    const pending = service.listAuditLog('o1');
    expect(service.isLoading()).toBe(true);

    const req = httpMock.expectOne(url('o1', 0, 100));
    expect(req.request.method).toBe('GET');
    req.flush([entry('a1')]);

    await pending;
    expect(service.entries().length).toBe(1);
    expect(service.error()).toBeNull();
    expect(service.isLoading()).toBe(false);
  });

  it('forwards an explicit paging window verbatim', async () => {
    const pending = service.listAuditLog('o1', 200, 50);
    httpMock.expectOne(url('o1', 200, 50)).flush([]);
    await pending;
    expect(service.entries()).toEqual([]);
  });

  // The backend clamps take to 200; the client sends what it is given. Pinned so that adding a
  // client-side clamp is a deliberate change.
  it('does not clamp take client-side — the server enforces the 200 cap', async () => {
    const pending = service.listAuditLog('o1', 0, 1000);
    httpMock.expectOne(url('o1', 0, 1000)).flush([]);
    await pending;
  });

  it('treats a null body as an empty list', async () => {
    const pending = service.listAuditLog('o1');
    httpMock.expectOne(url('o1', 0, 100)).flush(null);
    await pending;
    expect(service.entries()).toEqual([]);
  });

  it('sets the error signal and stops loading on failure', async () => {
    const pending = service.listAuditLog('o1');
    httpMock.expectOne(url('o1', 0, 100)).flush(null, { status: 403, statusText: 'Forbidden' });
    await pending;

    expect(service.error()).toBe('Failed to load audit log');
    expect(service.isLoading()).toBe(false);
  });
});
