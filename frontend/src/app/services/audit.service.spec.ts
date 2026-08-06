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

  // The server clamps take to 200 silently, so the client clamps too — otherwise a caller that
  // asked for 1000 and advanced by 1000 would step over the 800 rows it never received.
  it('clamps take to the servers 200 cap before sending', async () => {
    const pending = service.listAuditLog('o1', 0, 1000);
    httpMock.expectOne(url('o1', 0, 200)).flush([]);
    await pending;
  });

  it('floors a negative skip and a non-positive take', async () => {
    const pending = service.listAuditLog('o1', -5, 0);
    httpMock.expectOne(url('o1', 0, 1)).flush([]);
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
