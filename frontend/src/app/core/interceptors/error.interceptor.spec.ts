import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { NavigationExtras, Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { authInterceptor } from './auth.interceptor';
import { errorInterceptor } from './error.interceptor';
import { AuthService } from '../../services/auth.service';
import { ErrorService } from '../../services/error.service';
import {
  REFRESH_TOKEN_KEY,
  TOKEN_KEY,
  USER_KEY,
  readAccessToken,
  readRefreshToken,
} from '../utils/auth-storage';
import { environment } from '../../../environments/environment';
import { User } from '../models';

const API = environment.apiUrl;

const USER: User = {
  id: 'u1',
  orgId: 'o1',
  email: 'someone@example.com',
  displayName: 'Someone',
  avatarUrl: null,
  role: 'developer',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

function authResponse(token: string, refreshToken: string) {
  return { token, refreshToken, expiresInSeconds: 900, user: USER };
}

function seedSession(token = 'access-1', refreshToken = 'refresh-1'): void {
  localStorage.setItem(TOKEN_KEY, token);
  localStorage.setItem(REFRESH_TOKEN_KEY, refreshToken);
  localStorage.setItem(USER_KEY, JSON.stringify(USER));
}

describe('errorInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let router: Router;
  let authService: AuthService;
  let errorService: ErrorService;
  let navigatedTo: unknown[][];

  beforeEach(() => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        // A catch-all so router.url can be set to a real attempted URL for the returnUrl assertions.
        provideRouter([{ path: '**', children: [] }]),
        // Same interceptor chain and order as app.config.ts: authInterceptor attaches the token,
        // errorInterceptor sits closest to the backend so it can replay a refreshed request.
        provideHttpClient(withInterceptors([authInterceptor, errorInterceptor])),
        provideHttpClientTesting(),
        // No provideTranslateHttpLoader(): ngx-translate falls back to a synchronous in-memory
        // loader, so translate.instant() returns the key itself — enough to assert on.
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    authService = TestBed.inject(AuthService);
    errorService = TestBed.inject(ErrorService);

    navigatedTo = [];
    vi.spyOn(router, 'navigate').mockImplementation(
      (commands: readonly unknown[], extras?: NavigationExtras) => {
        navigatedTo.push([commands, extras]);
        return Promise.resolve(true);
      },
    );
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
    vi.restoreAllMocks();
  });

  it('refreshes once and replays the original request on a 401', () => {
    seedSession();
    authService.loadFromStorage();

    let body: unknown = null;
    http.get('/api/runs').subscribe((res) => (body = res));

    httpMock.expectOne('/api/runs').flush(null, { status: 401, statusText: 'Unauthorized' });

    const refresh = httpMock.expectOne(`${API}/api/auth/refresh`);
    expect(refresh.request.body).toEqual({ refreshToken: 'refresh-1' });
    refresh.flush(authResponse('access-2', 'refresh-2'));

    // The replay must carry the *new* token: it bypasses authInterceptor.
    const replay = httpMock.expectOne('/api/runs');
    expect(replay.request.headers.get('Authorization')).toBe('Bearer access-2');
    replay.flush({ ok: true });

    expect(body).toEqual({ ok: true });
    expect(readAccessToken()).toBe('access-2');
    expect(readRefreshToken()).toBe('refresh-2');
    expect(navigatedTo.length).toBe(0);
  });

  it('shares a single in-flight refresh across concurrent 401s', () => {
    seedSession();
    authService.loadFromStorage();

    const seen: string[] = [];
    http.get<{ from: string }>('/api/runs').subscribe((r) => seen.push(r.from));
    http.get<{ from: string }>('/api/projects').subscribe((r) => seen.push(r.from));
    http.get<{ from: string }>('/api/agents').subscribe((r) => seen.push(r.from));

    for (const url of ['/api/runs', '/api/projects', '/api/agents']) {
      httpMock.expectOne(url).flush(null, { status: 401, statusText: 'Unauthorized' });
    }

    // Exactly one refresh: rotation means a second one would present an already-revoked token
    // and make the server revoke the whole family.
    const refreshes = httpMock.match(`${API}/api/auth/refresh`);
    expect(refreshes.length).toBe(1);
    refreshes[0].flush(authResponse('access-2', 'refresh-2'));

    for (const url of ['/api/runs', '/api/projects', '/api/agents']) {
      const replay = httpMock.expectOne(url);
      expect(replay.request.headers.get('Authorization')).toBe('Bearer access-2');
      replay.flush({ from: url });
    }

    expect(seen.sort()).toEqual(['/api/agents', '/api/projects', '/api/runs']);
  });

  it('starts a fresh refresh for a later 401 once the first one has settled', () => {
    seedSession();
    authService.loadFromStorage();

    http.get('/api/runs').subscribe({ next: () => undefined });
    httpMock.expectOne('/api/runs').flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne(`${API}/api/auth/refresh`).flush(authResponse('access-2', 'refresh-2'));
    httpMock.expectOne('/api/runs').flush({});

    http.get('/api/projects').subscribe({ next: () => undefined });
    httpMock.expectOne('/api/projects').flush(null, { status: 401, statusText: 'Unauthorized' });

    // The shared observable was released, so this 401 gets its own refresh — with the rotated token.
    const second = httpMock.expectOne(`${API}/api/auth/refresh`);
    expect(second.request.body).toEqual({ refreshToken: 'refresh-2' });
    second.flush(authResponse('access-3', 'refresh-3'));
    httpMock.expectOne('/api/projects').flush({});

    expect(readAccessToken()).toBe('access-3');
  });

  it('logs out and redirects with a returnUrl when the refresh is refused', async () => {
    seedSession();
    authService.loadFromStorage();
    await router.navigateByUrl('/runs/abc');

    let status = 0;
    http.get('/api/runs/abc').subscribe({ error: (err) => (status = err.status) });

    httpMock.expectOne('/api/runs/abc').flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock
      .expectOne(`${API}/api/auth/refresh`)
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(status).toBe(401);
    expect(readAccessToken()).toBeNull();
    expect(readRefreshToken()).toBeNull();
    expect(authService.isAuthenticated()).toBe(false);
    expect(navigatedTo[0][0]).toEqual(['/login']);
    expect(navigatedTo[0][1]).toEqual({ queryParams: { returnUrl: '/runs/abc' } });
  });

  it('does not attempt a refresh when there is no refresh token', () => {
    localStorage.setItem(TOKEN_KEY, 'access-1');
    localStorage.setItem(USER_KEY, JSON.stringify(USER));
    authService.loadFromStorage();

    http.get('/api/runs').subscribe({ error: () => undefined });
    httpMock.expectOne('/api/runs').flush(null, { status: 401, statusText: 'Unauthorized' });

    httpMock.expectNone(`${API}/api/auth/refresh`);
    expect(readAccessToken()).toBeNull();
    expect(navigatedTo.length).toBe(1);
  });

  it('leaves a failed login 401 to the login form: no refresh, no logout, no redirect', () => {
    // A wrong password is a legitimate 401 that says nothing about session expiry.
    let status = 0;
    http
      .post(`${API}/api/auth/login`, { email: 'a@b.c', password: 'nope' })
      .subscribe({ error: (err) => (status = err.status) });

    httpMock
      .expectOne(`${API}/api/auth/login`)
      .flush({ error: 'Invalid credentials' }, { status: 401, statusText: 'Unauthorized' });

    expect(status).toBe(401);
    httpMock.expectNone(`${API}/api/auth/refresh`);
    expect(navigatedTo.length).toBe(0);
  });

  it('does not refresh-loop when the refresh endpoint itself 401s', () => {
    seedSession();
    authService.loadFromStorage();

    authService.refreshAccessToken().subscribe({ error: () => undefined });
    httpMock
      .expectOne(`${API}/api/auth/refresh`)
      .flush(null, { status: 401, statusText: 'Unauthorized' });

    // Exempt from the refresh-and-replay path, so no second refresh was issued.
    httpMock.expectNone(`${API}/api/auth/refresh`);
  });

  it('keeps the session on a 403 and reports an insufficient-permissions message', () => {
    seedSession();
    authService.loadFromStorage();

    let status = 0;
    http.get('/api/secrets').subscribe({ error: (err) => (status = err.status) });
    httpMock.expectOne('/api/secrets').flush(null, { status: 403, statusText: 'Forbidden' });

    expect(status).toBe(403);
    httpMock.expectNone(`${API}/api/auth/refresh`);
    expect(readAccessToken()).toBe('access-1');
    expect(authService.isAuthenticated()).toBe(true);
    expect(navigatedTo.length).toBe(0);
    expect(errorService.lastError()).toBe('errors.forbidden');
  });

  it('reports other failures into the shared error signal and rethrows', () => {
    seedSession();
    authService.loadFromStorage();

    let status = 0;
    http.get('/api/runs').subscribe({ error: (err) => (status = err.status) });
    httpMock
      .expectOne('/api/runs')
      .flush({ error: 'Agent not found' }, { status: 404, statusText: 'Not Found' });

    expect(status).toBe(404);
    expect(errorService.lastError()).toBe('Agent not found');
    expect(authService.isAuthenticated()).toBe(true);
  });
});
