import { Injectable, signal } from '@angular/core';

/** Shared last-error signal, populated centrally by the error interceptor. */
@Injectable({ providedIn: 'root' })
export class ErrorService {
  readonly lastError = signal<string | null>(null);

  report(message: string): void {
    this.lastError.set(message);
  }

  clear(): void {
    this.lastError.set(null);
  }
}
