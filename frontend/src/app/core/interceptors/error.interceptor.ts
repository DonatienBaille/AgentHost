import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { Injector, inject } from '@angular/core';
import { Router } from '@angular/router';
import { TranslateService } from '@ngx-translate/core';
import { Observable, catchError, switchMap, throwError } from 'rxjs';
import { ErrorService } from '../../services/error.service';
import {
  AuthService,
  LOGIN_PATH,
  LOGOUT_PATH,
  REFRESH_PATH,
  REGISTER_PATH,
} from '../../services/auth.service';

/**
 * Requests whose own 401 is a *result*, not an expired session:
 *  - login/register: wrong credentials — the form shows an inline error, logging the user out and
 *    bouncing them to /login (where they already are) would be nonsense;
 *  - refresh: the refresh token itself is dead, so retrying the refresh can only loop;
 *  - logout: the session is being torn down anyway.
 */
const AUTH_EXEMPT_PATHS = [LOGIN_PATH, REGISTER_PATH, REFRESH_PATH, LOGOUT_PATH];

function isAuthExempt(url: string): boolean {
  return AUTH_EXEMPT_PATHS.some((path) => url.startsWith(path));
}

/**
 * Centralizes HTTP error handling:
 *  - 401 on a normal API call: try a single (shared) silent refresh and replay the request; if the
 *    refresh fails, clear the session and bounce to /login preserving the attempted URL;
 *  - 403: the caller is authenticated but under-privileged — surface a translated message and do
 *    NOT log them out;
 *  - anything else: report the message into the shared error signal, as before.
 *
 * Everything is rethrown so callers keep their own error handling.
 *
 * Circular DI: AuthService's own HTTP calls pass through this interceptor, so injecting it
 * eagerly here would make Angular construct AuthService while it is already being constructed.
 * We capture an Injector instead and resolve AuthService lazily, inside the error handler.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const injector = inject(Injector);
  const errorService = inject(ErrorService);

  return next(req).pipe(
    catchError((err: HttpErrorResponse) => {
      if (err.status === 401 && !isAuthExempt(req.url)) {
        return handleExpiredSession(req, next, err, injector);
      }

      if (err.status === 403) {
        // Authenticated, just not allowed. Never clears the session.
        errorService.report(translate(injector, 'errors.forbidden'));
        return throwError(() => err);
      }

      errorService.report(extractMessage(err, injector));
      return throwError(() => err);
    }),
  );
};

/**
 * One silent refresh (shared across every concurrent 401 by AuthService), then a replay of the
 * original request carrying the new token.
 */
function handleExpiredSession(
  req: HttpRequest<unknown>,
  next: HttpHandlerFn,
  originalError: HttpErrorResponse,
  injector: Injector,
): Observable<HttpEvent<unknown>> {
  const authService = injector.get(AuthService);

  return authService.refreshAccessToken().pipe(
    catchError(() => {
      // Refresh refused (or nothing to refresh with): the session is over.
      redirectToLogin(injector);
      return throwError(() => originalError);
    }),
    switchMap((token) =>
      // The replay bypasses authInterceptor (we are downstream of it), so set the header here.
      next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })).pipe(
        catchError((replayError: HttpErrorResponse) => {
          if (replayError.status === 401) {
            // A brand-new token that is still rejected means the session is genuinely gone.
            authService.clearSession();
            redirectToLogin(injector);
          } else {
            injector.get(ErrorService).report(extractMessage(replayError, injector));
          }
          return throwError(() => replayError);
        }),
      ),
    ),
  );
}

/**
 * Clears local auth state and sends the user to /login, remembering where they were so the login
 * form can send them back (see LoginComponent / authGuard, which use the same `returnUrl` param).
 */
function redirectToLogin(injector: Injector): void {
  const authService = injector.get(AuthService);
  const router = injector.get(Router);

  authService.clearSession();
  injector.get(ErrorService).report(translate(injector, 'errors.sessionExpired'));

  const attemptedUrl = router.url;
  const queryParams =
    attemptedUrl && attemptedUrl !== '/' && !attemptedUrl.startsWith('/login')
      ? { returnUrl: attemptedUrl }
      : {};

  void router.navigate(['/login'], { queryParams });
}

function translate(injector: Injector, key: string): string {
  return injector.get(TranslateService).instant(key);
}

function extractMessage(err: HttpErrorResponse, injector: Injector): string {
  if (err.status === 0) {
    return translate(injector, 'errors.network');
  }
  if (typeof err.error === 'string' && err.error.trim().length > 0) {
    return err.error;
  }
  if (err.error && typeof err.error === 'object') {
    const body = err.error as { message?: unknown; error?: unknown };
    if (typeof body.error === 'string' && body.error.trim().length > 0) {
      return body.error;
    }
    if (body.message !== undefined && body.message !== null) {
      return String(body.message);
    }
  }
  return `${err.status} ${err.statusText || translate(injector, 'errors.requestFailed')}`;
}
