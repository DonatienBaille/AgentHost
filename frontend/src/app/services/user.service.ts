import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateUserRequest, UpdateUserRequest, User } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/users`;

@Injectable({ providedIn: 'root' })
export class UserService {
  private readonly http = inject(HttpClient);

  readonly users = signal<User[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  /** Lists the caller's own organization's users — the server scopes by the JWT, no orgId param. */
  async listUsers(): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(this.http.get<User[]>(BASE_URL));
      this.users.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('errors.loadUsers');
    } finally {
      this.isLoading.set(false);
    }
  }

  async createUser(req: CreateUserRequest): Promise<User> {
    const user = await firstValueFrom(this.http.post<User>(BASE_URL, req));
    this.users.set([...this.users(), user]);
    return user;
  }

  /** Mise à jour partielle d'un membre (rôle, nom affiché, mot de passe). */
  async updateUser(id: string, req: UpdateUserRequest): Promise<User> {
    const updated = await firstValueFrom(this.http.put<User>(`${BASE_URL}/${id}`, req));
    this.users.set(this.users().map((u) => (u.id === id ? updated : u)));
    return updated;
  }

  /**
   * Suppression logique côté serveur. La ligne disparaît de la liste locale : le serveur ne
   * renvoie pas de corps et re-lister pour un seul retrait serait un aller-retour de trop.
   */
  async deleteUser(id: string): Promise<void> {
    await firstValueFrom(this.http.delete<void>(`${BASE_URL}/${id}`));
    this.users.set(this.users().filter((u) => u.id !== id));
  }
}
