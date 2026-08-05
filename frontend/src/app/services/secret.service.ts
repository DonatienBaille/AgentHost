import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateSecretRequest, Secret } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/secrets`;

@Injectable({ providedIn: 'root' })
export class SecretService {
  private readonly http = inject(HttpClient);

  readonly secrets = signal<Secret[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  /**
   * NOTE: as of this writing the backend only exposes POST /api/secrets
   * (Endpoints/SecretEndpoints.cs) — GET (list) and DELETE are expected from the
   * concurrent backend work described in this task but are not yet merged. This
   * call will 404 until that lands.
   */
  async listSecrets(orgId: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(
        this.http.get<Secret[]>(`${BASE_URL}?orgId=${encodeURIComponent(orgId)}`),
      );
      this.secrets.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('Failed to load secrets');
    } finally {
      this.isLoading.set(false);
    }
  }

  async createSecret(req: CreateSecretRequest): Promise<Secret> {
    const secret = await firstValueFrom(this.http.post<Secret>(BASE_URL, req));
    this.secrets.set([...this.secrets(), secret]);
    return secret;
  }

  /** NOTE: not yet present on the backend snapshot in this worktree — see listSecrets(). */
  async deleteSecret(id: string): Promise<void> {
    await firstValueFrom(this.http.delete<void>(`${BASE_URL}/${id}`));
    this.secrets.set(this.secrets().filter((s) => s.id !== id));
  }
}
