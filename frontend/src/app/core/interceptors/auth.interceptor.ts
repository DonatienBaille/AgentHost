import { HttpInterceptorFn } from '@angular/common/http';

const TOKEN_KEY = 'agenthost_token';

/** Attaches a Bearer token from localStorage to outgoing API requests, if present. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = localStorage.getItem(TOKEN_KEY);
  if (!token) {
    return next(req);
  }

  const authReq = req.clone({
    setHeaders: { Authorization: `Bearer ${token}` },
  });

  return next(authReq);
};
