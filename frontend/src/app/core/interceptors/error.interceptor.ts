import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, throwError } from 'rxjs';
import { ErrorService } from '../../services/error.service';

/** Centralizes surfacing of HTTP errors into a shared signal, then rethrows. */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  const errorService = inject(ErrorService);

  return next(req).pipe(
    catchError((err: HttpErrorResponse) => {
      const message = extractMessage(err);
      errorService.report(message);
      return throwError(() => err);
    }),
  );
};

function extractMessage(err: HttpErrorResponse): string {
  if (err.status === 0) {
    return 'Network error: unable to reach the server.';
  }
  if (typeof err.error === 'string' && err.error.trim().length > 0) {
    return err.error;
  }
  if (err.error && typeof err.error === 'object' && 'message' in err.error) {
    return String((err.error as { message?: unknown }).message);
  }
  return `${err.status} ${err.statusText || 'Request failed'}`;
}
