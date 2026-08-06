import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import {
  ActivatedRouteSnapshot,
  RouterStateSnapshot,
  UrlTree,
  provideRouter,
} from '@angular/router';
import { authGuard } from './auth.guard';
import { AuthService } from '../../services/auth.service';
import { User } from '../models';

function user(): User {
  return {
    id: 'u1',
    orgId: 'o1',
    email: 'a@b.c',
    displayName: null,
    avatarUrl: null,
    role: 'developer',
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
  };
}

/** Runs the guard in an injection context, as the router would. */
function runGuard(url: string): boolean | UrlTree {
  const route = {} as ActivatedRouteSnapshot;
  const state = { url } as RouterStateSnapshot;
  return TestBed.runInInjectionContext(
    () => authGuard(route, state) as boolean | UrlTree,
  );
}

describe('authGuard', () => {
  let auth: AuthService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    auth = TestBed.inject(AuthService);
  });

  afterEach(() => localStorage.clear());

  it('lets an authenticated user through', () => {
    auth.currentUser.set(user());
    expect(runGuard('/projects')).toBe(true);
  });

  describe('anonymous', () => {
    // The guard signals the redirect with a UrlTree — it does not navigate imperatively.
    it('returns a UrlTree pointing at /login rather than navigating', () => {
      const result = runGuard('/projects');

      expect(result).toBeInstanceOf(UrlTree);
      const tree = result as UrlTree;
      expect(tree.toString().startsWith('/login')).toBe(true);
    });

    it('preserves the attempted URL as returnUrl', () => {
      const tree = runGuard('/projects/p1/runs') as UrlTree;
      expect(tree.queryParams['returnUrl']).toBe('/projects/p1/runs');
    });

    it('keeps query params of the attempted URL inside returnUrl', () => {
      const tree = runGuard('/runs?status=failed') as UrlTree;
      expect(tree.queryParams['returnUrl']).toBe('/runs?status=failed');
    });

    it('omits returnUrl for the root URL — nothing useful to return to', () => {
      const tree = runGuard('/') as UrlTree;
      expect(tree.queryParams['returnUrl']).toBeUndefined();
    });

    it('omits returnUrl when the attempted URL is already the login page', () => {
      const tree = runGuard('/login') as UrlTree;
      expect(tree.queryParams['returnUrl']).toBeUndefined();
    });

    it('omits returnUrl for a login URL that already carries a returnUrl', () => {
      const tree = runGuard('/login?returnUrl=%2Fprojects') as UrlTree;
      expect(tree.queryParams['returnUrl']).toBeUndefined();
    });
  });

  it('re-evaluates per call — logging out after a pass blocks the next navigation', () => {
    auth.currentUser.set(user());
    expect(runGuard('/projects')).toBe(true);

    auth.clearSession();
    expect(runGuard('/projects')).toBeInstanceOf(UrlTree);
  });
});
