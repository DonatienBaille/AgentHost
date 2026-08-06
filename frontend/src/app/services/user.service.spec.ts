import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { UserService } from './user.service';
import { environment } from '../../environments/environment';
import { User } from '../core/models';

const URL = `${environment.apiUrl}/api/users`;

function user(id: string, email = 'a@b.c'): User {
  return {
    id,
    orgId: 'o1',
    email,
    displayName: null,
    avatarUrl: null,
    role: 'developer',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

describe('UserService', () => {
  let service: UserService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(UserService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('lists without an orgId query param — the server scopes by the JWT', async () => {
    const pending = service.listUsers();
    expect(service.isLoading()).toBe(true);

    const req = httpMock.expectOne(URL);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.keys()).toEqual([]);
    req.flush([user('u1'), user('u2', 'd@e.f')]);

    await pending;
    expect(service.users().length).toBe(2);
    expect(service.error()).toBeNull();
    expect(service.isLoading()).toBe(false);
  });

  it('sets the error signal and stops loading when the list fails', async () => {
    const pending = service.listUsers();
    httpMock.expectOne(URL).flush(null, { status: 403, statusText: 'Forbidden' });
    await pending;

    expect(service.error()).toBe('errors.loadUsers');
    expect(service.isLoading()).toBe(false);
  });

  it('treats a null list body as empty', async () => {
    const pending = service.listUsers();
    httpMock.expectOne(URL).flush(null);
    await pending;
    expect(service.users()).toEqual([]);
  });

  // REGRESSION: the server creates the user inside the caller's own org, read from the JWT.
  it('createUser never sends orgId or a client-supplied identity field in the body', async () => {
    const pending = service.createUser({
      email: 'new@example.com',
      password: 'pw',
      role: 'viewer',
    });

    const req = httpMock.expectOne(URL);
    expect(req.request.method).toBe('POST');
    const keys = Object.keys(req.request.body as object);
    expect(keys).not.toContain('orgId');
    expect(keys).not.toContain('organizationId');
    expect(keys).not.toContain('createdByUserId');
    expect(keys).not.toContain('invitedByUserId');
    expect(req.request.body).toEqual({
      email: 'new@example.com',
      password: 'pw',
      role: 'viewer',
    });

    req.flush(user('u1', 'new@example.com'));
    await pending;
    expect(service.users().length).toBe(1);
  });

  it('propagates a create failure to the caller', async () => {
    const pending = service.createUser({ email: 'x@y.z', password: 'pw', role: 'owner' });
    httpMock.expectOne(URL).flush(null, { status: 409, statusText: 'Conflict' });

    await expect(pending).rejects.toBeTruthy();
    expect(service.users()).toEqual([]);
  });
});
