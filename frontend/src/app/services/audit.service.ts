import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuditLogEntry } from '../core/models';

@Injectable({ providedIn: 'root' })
export class AuditService {
  private readonly http = inject(HttpClient);

  readonly entries = signal<AuditLogEntry[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  async listAuditLog(orgId: string, skip = 0, take = 100): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(
        this.http.get<AuditLogEntry[]>(
          `${environment.apiUrl}/api/organizations/${orgId}/audit-log?skip=${skip}&take=${take}`,
        ),
      );
      this.entries.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('Failed to load audit log');
    } finally {
      this.isLoading.set(false);
    }
  }
}
