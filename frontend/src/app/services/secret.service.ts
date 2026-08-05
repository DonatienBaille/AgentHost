import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateSecretRequest, Secret, UpdateSecretRequest } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/secrets`;

/**
 * Secret *metadata* management. Mirrors backend Endpoints/SecretEndpoints.cs: list/get/create/
 * rotate/delete, all maintainer+, all scoped to the caller's organization via the JWT. Values are
 * write-only — no response ever contains one, so there is nothing to display or cache.
 */
@Injectable({ providedIn: 'root' })
export class SecretService {
  private readonly http = inject(HttpClient);

  readonly secrets = signal<Secret[]>([]);
  readonly isLoading = signal(false);
  /** Translation key of the last load failure, or null. */
  readonly error = signal<string | null>(null);

  async listSecrets(): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(this.http.get<Secret[]>(BASE_URL));
      this.secrets.set(data ?? []);
      this.error.set(null);
    } catch {
      this.secrets.set([]);
      this.error.set('adminSecrets.loadError');
    } finally {
      this.isLoading.set(false);
    }
  }

  async getSecret(id: string): Promise<Secret> {
    return await firstValueFrom(this.http.get<Secret>(`${BASE_URL}/${id}`));
  }

  async createSecret(req: CreateSecretRequest): Promise<Secret> {
    const secret = await firstValueFrom(this.http.post<Secret>(BASE_URL, req));
    this.secrets.set([...this.secrets(), secret]);
    return secret;
  }

  /** Replaces the stored value (PUT /api/secrets/{id}); the new value is never echoed back. */
  async rotateSecret(id: string, value: string): Promise<Secret> {
    const body: UpdateSecretRequest = { value };
    const secret = await firstValueFrom(this.http.put<Secret>(`${BASE_URL}/${id}`, body));
    this.secrets.set(this.secrets().map((s) => (s.id === secret.id ? secret : s)));
    return secret;
  }

  async deleteSecret(id: string): Promise<void> {
    await firstValueFrom(this.http.delete<void>(`${BASE_URL}/${id}`));
    this.secrets.set(this.secrets().filter((s) => s.id !== id));
  }
}
