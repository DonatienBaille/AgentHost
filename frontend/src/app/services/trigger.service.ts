import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  CreateTriggerRequest,
  CreateTriggerResponse,
  Trigger,
  UpdateTriggerRequest,
} from '../core/models';

/**
 * Les déclencheurs entrants d'un projet (feuille de route, lot 4).
 *
 * <b>Le secret rendu à la création n'est jamais mémorisé ici.</b> Il traverse la méthode et
 * ressort vers l'appelant, qui l'affiche une fois. Le stocker dans un signal le laisserait en
 * mémoire pour toute la session, et une navigation le ferait réapparaître — ce qui contredirait la
 * promesse « affiché une seule fois » que l'écran fait à l'utilisateur.
 */
@Injectable({ providedIn: 'root' })
export class TriggerService {
  private readonly http = inject(HttpClient);

  readonly triggers = signal<Trigger[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  async list(projectId: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(
        this.http.get<Trigger[]>(`${environment.apiUrl}/api/projects/${projectId}/triggers`),
      );
      this.triggers.set(data ?? []);
      this.error.set(null);
    } catch {
      this.error.set('errors.loadTriggers');
    } finally {
      this.isLoading.set(false);
    }
  }

  /**
   * Crée un déclencheur et rend la réponse complète, secret compris.
   *
   * Laisse remonter l'échec : l'appelant a un formulaire à garder ouvert et un message à afficher
   * à côté du champ fautif — une expression cron refusée n'est pas une panne de chargement.
   */
  async create(projectId: string, req: CreateTriggerRequest): Promise<CreateTriggerResponse> {
    const created = await firstValueFrom(
      this.http.post<CreateTriggerResponse>(
        `${environment.apiUrl}/api/projects/${projectId}/triggers`,
        req,
      ),
    );
    // La liste locale est mise à jour sans relire : le serveur vient de rendre l'objet créé.
    this.triggers.update((current) => [created.trigger, ...current]);
    return created;
  }

  async update(id: string, req: UpdateTriggerRequest): Promise<Trigger> {
    const updated = await firstValueFrom(
      this.http.patch<Trigger>(`${environment.apiUrl}/api/triggers/${id}`, req),
    );
    this.triggers.update((current) => current.map((t) => (t.id === updated.id ? updated : t)));
    return updated;
  }

  async remove(id: string): Promise<void> {
    await firstValueFrom(this.http.delete<void>(`${environment.apiUrl}/api/triggers/${id}`));
    this.triggers.update((current) => current.filter((t) => t.id !== id));
  }
}
