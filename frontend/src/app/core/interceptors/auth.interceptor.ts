import { HttpInterceptorFn } from '@angular/common/http';
import { readAccessToken } from '../utils/auth-storage';
import { ANONYMOUS_AUTH_PATHS } from '../../services/auth.service';

/** Attaches a Bearer token from localStorage to outgoing API requests, if present. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  // A caller that set Authorization itself meant it — most importantly the refresh-and-replay in
  // errorInterceptor, which replays with the *new* token while storage may still hold the old one.
  // Overwriting it would put the request back on the credential that just 401'd.
  if (req.headers.has('Authorization')) {
    return next(req);
  }

  // Credential exchanges carry their proof in the body; a stale access token on top adds nothing
  // and, on /mfa/verify, is precisely the half-authenticated session the challenge is gating.
  if (ANONYMOUS_AUTH_PATHS.some((path) => req.url.startsWith(path))) {
    return next(req);
  }

  const token = readAccessToken();
  if (!token) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
