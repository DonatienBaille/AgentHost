import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AuthService } from '../../services/auth.service';

/**
 * Redirects to /login when there is no authenticated session, preserving the attempted URL as a
 * `returnUrl` query param so LoginComponent can send the user back after signing in. The error
 * interceptor uses the same shape when a refresh fails.
 */
export const authGuard: CanActivateFn = (_route, state) => {
  const authService = inject(AuthService);
  const router = inject(Router);

  if (authService.isAuthenticated()) {
    return true;
  }

  const attemptedUrl = state.url;
  return router.createUrlTree(['/login'], {
    queryParams:
      attemptedUrl && attemptedUrl !== '/' && !attemptedUrl.startsWith('/login')
        ? { returnUrl: attemptedUrl }
        : {},
  });
};
