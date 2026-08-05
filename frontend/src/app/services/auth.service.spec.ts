import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { AuthService } from './auth.service';
import { REFRESH_TOKEN_KEY, TOKEN_KEY, USER_KEY } from '../core/utils/auth-storage';
import { environment } from '../../environments/environment';
import { User } from '../core/models';

const API = environment.apiUrl;

const USER: User = {
  id: 'u1',
  orgId: 'o1',
  email: 'someone@example.com',
  displayName: 'Someone',
  avatarUrl: null,
  role: 'maintainer',
  createdAt: '2026-01-01T00:00:00Z',
  updatedAt: '2026-01-01T00:00:00Z',
};

describe('AuthService', () => {
  let service: AuthService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [
        // logout() navigates to /login, so the test router needs something to match.
        provideRouter([{ path: '**', children: [] }]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
      ],
    });
    service = TestBed.inject(AuthService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    localStorage.clear();
  });

  it('persists both halves of the token pair on login', async () => {
    const pending = service.login('someone@example.com', 'pw');

    httpMock.expectOne(`${API}/api/auth/login`).flush({
      token: 'access-1',
      refreshToken: 'refresh-1',
      expiresInSeconds: 900,
      user: USER,
    });

    await pending;
    expect(localStorage.getItem(TOKEN_KEY)).toBe('access-1');
    expect(localStorage.getItem(REFRESH_TOKEN_KEY)).toBe('refresh-1');
    expect(service.currentUser()?.id).toBe('u1');
    expect(service.isMaintainerOrAbove()).toBe(true);
  });

  it('revokes refresh tokens server-side before clearing local state', async () => {
    localStorage.setItem(TOKEN_KEY, 'access-1');
    localStorage.setItem(REFRESH_TOKEN_KEY, 'refresh-1');
    localStorage.setItem(USER_KEY, JSON.stringify(USER));
    service.loadFromStorage();

    const pending = service.logout();
    httpMock.expectOne(`${API}/api/auth/logout`).flush({ revoked: 2 });
    await pending;

    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
    expect(localStorage.getItem(REFRESH_TOKEN_KEY)).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
  });

  it('still clears the local session when the logout call fails', async () => {
    localStorage.setItem(TOKEN_KEY, 'access-1');
    localStorage.setItem(REFRESH_TOKEN_KEY, 'refresh-1');
    localStorage.setItem(USER_KEY, JSON.stringify(USER));
    service.loadFromStorage();

    const pending = service.logout();
    httpMock
      .expectOne(`${API}/api/auth/logout`)
      .flush(null, { status: 500, statusText: 'Server Error' });
    await pending;

    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
    expect(service.isAuthenticated()).toBe(false);
  });

  it('drops a token whose stored user is unreadable', () => {
    localStorage.setItem(TOKEN_KEY, 'access-1');
    localStorage.setItem(USER_KEY, 'not json');

    service.loadFromStorage();

    expect(service.isAuthenticated()).toBe(false);
    expect(localStorage.getItem(TOKEN_KEY)).toBeNull();
  });
});
