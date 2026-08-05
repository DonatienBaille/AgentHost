import { HttpInterceptorFn } from '@angular/common/http';
import { readAccessToken } from '../utils/auth-storage';

/** Attaches a Bearer token from localStorage to outgoing API requests, if present. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = readAccessToken();
  if (!token) {
    return next(req);
  }

  const authReq = req.clone({
    setHeaders: { Authorization: `Bearer ${token}` },
  });

  return next(authReq);
};
