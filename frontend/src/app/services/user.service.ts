import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateUserRequest, User } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/users`;

@Injectable({ providedIn: 'root' })
export class UserService {
  private readonly http = inject(HttpClient);

  readonly users = signal<User[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  async listUsers(orgId: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(
        this.http.get<User[]>(`${BASE_URL}?orgId=${encodeURIComponent(orgId)}`),
      );
      this.users.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('Failed to load users');
    } finally {
      this.isLoading.set(false);
    }
  }

  async createUser(req: CreateUserRequest): Promise<User> {
    const user = await firstValueFrom(this.http.post<User>(BASE_URL, req));
    this.users.set([...this.users(), user]);
    return user;
  }
}
