import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthResponse, LoginRequest, RegisterRequest, User, UserRole } from '../core/models';
import { hasRoleAtLeast } from '../core/utils/roles';

const BASE_URL = `${environment.apiUrl}/api/auth`;

// Matches core/interceptors/auth.interceptor.ts — keep this key in sync.
const TOKEN_KEY = 'agenthost_token';
const USER_KEY = 'agenthost_user';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);

  readonly currentUser = signal<User | null>(null);
  readonly isAuthenticated = computed(() => !!this.currentUser());

  readonly isOwner = computed(() => this.currentUser()?.role === 'owner');
  readonly isMaintainerOrAbove = computed(() =>
    hasRoleAtLeast(this.currentUser()?.role, 'maintainer'),
  );
  readonly isDeveloperOrAbove = computed(() =>
    hasRoleAtLeast(this.currentUser()?.role, 'developer'),
  );

  hasRole(min: UserRole): boolean {
    return hasRoleAtLeast(this.currentUser()?.role, min);
  }

  async login(email: string, password: string): Promise<User> {
    const body: LoginRequest = { email, password };
    const res = await firstValueFrom(this.http.post<AuthResponse>(`${BASE_URL}/login`, body));
    this.persist(res);
    return res.user;
  }

  async register(
    orgName: string,
    orgSlug: string,
    email: string,
    password: string,
    displayName?: string,
  ): Promise<User> {
    const body: RegisterRequest = { orgName, orgSlug, email, password, displayName };
    const res = await firstValueFrom(this.http.post<AuthResponse>(`${BASE_URL}/register`, body));
    this.persist(res);
    return res.user;
  }

  logout(): void {
    localStorage.removeItem(TOKEN_KEY);
    localStorage.removeItem(USER_KEY);
    this.currentUser.set(null);
    this.router.navigate(['/login']);
  }

  /** Restores the session from localStorage on app bootstrap (e.g. after a page refresh). */
  loadFromStorage(): void {
    const token = localStorage.getItem(TOKEN_KEY);
    const rawUser = localStorage.getItem(USER_KEY);
    if (!token || !rawUser) {
      return;
    }
    try {
      this.currentUser.set(JSON.parse(rawUser) as User);
    } catch {
      localStorage.removeItem(TOKEN_KEY);
      localStorage.removeItem(USER_KEY);
      this.currentUser.set(null);
    }
  }

  private persist(res: AuthResponse): void {
    localStorage.setItem(TOKEN_KEY, res.token);
    localStorage.setItem(USER_KEY, JSON.stringify(res.user));
    this.currentUser.set(res.user);
  }
}
