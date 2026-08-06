import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateWebhookRequest, UpdateWebhookRequest, Webhook } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/webhooks`;

@Injectable({ providedIn: 'root' })
export class WebhookService {
  private readonly http = inject(HttpClient);

  readonly webhooks = signal<Webhook[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  async listWebhooks(projectId: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(
        this.http.get<Webhook[]>(`${BASE_URL}?projectId=${encodeURIComponent(projectId)}`),
      );
      this.webhooks.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('errors.loadWebhooks');
    } finally {
      this.isLoading.set(false);
    }
  }

  async createWebhook(req: CreateWebhookRequest): Promise<Webhook> {
    const webhook = await firstValueFrom(this.http.post<Webhook>(BASE_URL, req));
    this.webhooks.set([...this.webhooks(), webhook]);
    return webhook;
  }

  /** NOTE: PUT /api/webhooks/{id} is expected from the concurrent backend work; not yet merged. */
  async updateWebhook(id: string, req: UpdateWebhookRequest): Promise<Webhook> {
    const webhook = await firstValueFrom(this.http.put<Webhook>(`${BASE_URL}/${id}`, req));
    this.webhooks.set(this.webhooks().map((w) => (w.id === id ? webhook : w)));
    return webhook;
  }

  async toggleActive(webhook: Webhook): Promise<void> {
    await this.updateWebhook(webhook.id, { isActive: !webhook.isActive });
  }

  /** NOTE: DELETE /api/webhooks/{id} is expected from the concurrent backend work; not yet merged. */
  async deleteWebhook(id: string): Promise<void> {
    await firstValueFrom(this.http.delete<void>(`${BASE_URL}/${id}`));
    this.webhooks.set(this.webhooks().filter((w) => w.id !== id));
  }
}
