import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { authInterceptor } from './auth.interceptor';
import { REFRESH_TOKEN_KEY, TOKEN_KEY } from '../utils/auth-storage';
import { environment } from '../../../environments/environment';

const API = environment.apiUrl;

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('attaches the stored access token as a Bearer header', async () => {
    localStorage.setItem(TOKEN_KEY, 'jwt-abc');

    const pending = firstValueFrom(http.get(`${API}/api/projects`));
    const req = httpMock.expectOne(`${API}/api/projects`);
    expect(req.request.headers.get('Authorization')).toBe('Bearer jwt-abc');
    req.flush([]);
    await pending;
  });

  it('leaves the request untouched when no token is stored', async () => {
    const pending = firstValueFrom(http.get(`${API}/api/auth/login`));
    const req = httpMock.expectOne(`${API}/api/auth/login`);
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({});
    await pending;
  });

  it('reads the token from the agenthost_token key only', async () => {
    // A refresh token alone must not authenticate a request.
    localStorage.setItem(REFRESH_TOKEN_KEY, 'refresh-xyz');

    const pending = firstValueFrom(http.get(`${API}/api/projects`));
    const req = httpMock.expectOne(`${API}/api/projects`);
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush([]);
    await pending;
  });

  // The interceptor has no path carve-out: it signs every outgoing request whenever a token
  // exists, auth routes included. Pinned so removing or adding a carve-out is deliberate.
  it('also signs auth routes when a token is present — there is no path exemption', async () => {
    localStorage.setItem(TOKEN_KEY, 'jwt-abc');

    const pending = firstValueFrom(http.post(`${API}/api/auth/refresh`, {}));
    const req = httpMock.expectOne(`${API}/api/auth/refresh`);
    expect(req.request.headers.get('Authorization')).toBe('Bearer jwt-abc');
    req.flush({});
    await pending;
  });

  it('overwrites an Authorization header the caller set explicitly', async () => {
    localStorage.setItem(TOKEN_KEY, 'jwt-abc');

    const pending = firstValueFrom(
      http.get(`${API}/api/projects`, { headers: { Authorization: 'Basic caller' } }),
    );
    const req = httpMock.expectOne(`${API}/api/projects`);
    // setHeaders overwrites: the stored token wins.
    expect(req.request.headers.get('Authorization')).toBe('Bearer jwt-abc');
    req.flush([]);
    await pending;
  });

  it('re-reads storage per request — a token stored mid-session applies to later calls', async () => {
    const first = firstValueFrom(http.get(`${API}/api/projects`));
    const firstReq = httpMock.expectOne(`${API}/api/projects`);
    expect(firstReq.request.headers.has('Authorization')).toBe(false);
    firstReq.flush([]);
    await first;

    localStorage.setItem(TOKEN_KEY, 'jwt-later');
    const second = firstValueFrom(http.get(`${API}/api/projects`));
    const secondReq = httpMock.expectOne(`${API}/api/projects`);
    expect(secondReq.request.headers.get('Authorization')).toBe('Bearer jwt-later');
    secondReq.flush([]);
    await second;
  });

  it('preserves the request method, body and other headers', async () => {
    localStorage.setItem(TOKEN_KEY, 'jwt-abc');

    const pending = firstValueFrom(
      http.post(`${API}/api/projects`, { name: 'Alpha' }, { headers: { 'X-Trace': 't1' } }),
    );
    const req = httpMock.expectOne(`${API}/api/projects`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Alpha' });
    expect(req.request.headers.get('X-Trace')).toBe('t1');
    expect(req.request.headers.get('Authorization')).toBe('Bearer jwt-abc');
    req.flush({});
    await pending;
  });
});
