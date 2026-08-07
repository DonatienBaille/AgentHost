import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, catchError, finalize, firstValueFrom, map, of, shareReplay, tap, throwError } from 'rxjs';
import { environment } from '../../environments/environment';
import { AuthResponse, LoginRequest, RefreshRequest, RegisterRequest, User, UserRole } from '../core/models';
import { hasRoleAtLeast } from '../core/utils/roles';
import {
  clearSessionStorage,
  readAccessToken,
  readRefreshToken,
  readUser,
  writeSession,
} from '../core/utils/auth-storage';

const BASE_URL = `${environment.apiUrl}/api/auth`;

/** Auth routes that must never trigger the refresh-and-replay flow — see AUTH_EXEMPT_PATHS use. */
export const LOGIN_PATH = `${BASE_URL}/login`;
export const REGISTER_PATH = `${BASE_URL}/register`;
export const REFRESH_PATH = `${BASE_URL}/refresh`;
export const LOGOUT_PATH = `${BASE_URL}/logout`;

export const RESET_REQUEST_PATH = `${BASE_URL}/password-reset/request`;
export const RESET_CONFIRM_PATH = `${BASE_URL}/password-reset/confirm`;

/** Hors du groupe /api/auth : l'acceptation d'invitation vit sous /api/invitations. */
export const ACCEPT_INVITATION_PATH = `${environment.apiUrl}/api/invitations/accept`;

/**
 * The auth endpoints the server maps `AllowAnonymous` — credential *exchanges*, where the caller
 * proves itself with what is in the body (password, refresh token, reset token, MFA code) and a
 * stored access token means nothing.
 *
 * Deliberately an explicit list rather than a `/api/auth/*` prefix: the rest of that group —
 * `/me`, `/logout`, `/password`, `/mfa/enroll|confirm|disable` — is `RequireAuthorization` and
 * breaks outright without the Bearer header.
 */
export const ANONYMOUS_AUTH_PATHS = [
  LOGIN_PATH,
  REGISTER_PATH,
  REFRESH_PATH,
  RESET_REQUEST_PATH,
  RESET_CONFIRM_PATH,
  `${BASE_URL}/mfa/verify`,
];

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

  /**
   * The single in-flight refresh, shared by every 401'd request that is waiting on it.
   *
   * This is not just an optimisation: POST /api/auth/refresh *rotates* the pair and revokes the
   * token it was given, and replaying an already-revoked token makes the server revoke the whole
   * token family. Two concurrent refreshes would therefore log the user out rather than renew
   * their session.
   */
  private refreshInFlight: Observable<string> | null = null;

  hasRole(min: UserRole): boolean {
    return hasRoleAtLeast(this.currentUser()?.role, min);
  }

  async login(email: string, password: string): Promise<User> {
    const body: LoginRequest = { email, password };
    const res = await firstValueFrom(this.http.post<AuthResponse>(LOGIN_PATH, body));
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
    const res = await firstValueFrom(this.http.post<AuthResponse>(REGISTER_PATH, body));
    this.persist(res);
    return res.user;
  }

  /**
   * Demande un lien de réinitialisation. Ne renvoie **rien** d'exploitable, délibérément : le
   * serveur répond 202 que le compte existe ou non, pour ne pas révéler quelles adresses sont
   * enregistrées. L'IHM doit donc afficher le même message dans les deux cas.
   */
  async requestPasswordReset(email: string): Promise<void> {
    await firstValueFrom(this.http.post(RESET_REQUEST_PATH, { email }));
  }

  /**
   * Fixe un nouveau mot de passe à partir du jeton reçu par courriel. N'ouvre PAS de session : le
   * serveur ne renvoie pas de jetons ici, et c'est cohérent — quiconque tient le lien pourrait
   * sinon se connecter sans jamais prouver qu'il connaît le compte autrement.
   */
  async confirmPasswordReset(token: string, newPassword: string): Promise<void> {
    await firstValueFrom(this.http.post(RESET_CONFIRM_PATH, { token, newPassword }));
  }

  /**
   * Crée le compte de l'invité et **ouvre sa session** : contrairement à la réinitialisation, le
   * jeton d'invitation prouve qu'un membre de l'organisation a délibérément ouvert cet accès, et
   * l'invité vient de choisir son mot de passe. Le faire repasser par l'écran de connexion
   * n'ajouterait aucune garantie.
   */
  async acceptInvitation(token: string, password: string, displayName?: string): Promise<User> {
    const res = await firstValueFrom(
      this.http.post<AuthResponse>(ACCEPT_INVITATION_PATH, { token, password, displayName }),
    );
    this.persist(res);
    return res.user;
  }

  /** Current access token, for consumers that can't go through the HTTP interceptor (SignalR). */
  accessToken(): string | null {
    return readAccessToken();
  }

  /**
   * Renews the access token from the stored refresh token, sharing one HTTP call across all
   * concurrent callers. Emits the new access token; errors if there is nothing to refresh with or
   * the server refuses (in which case the local session has already been cleared).
   */
  refreshAccessToken(): Observable<string> {
    if (this.refreshInFlight) {
      return this.refreshInFlight;
    }

    const refreshToken = readRefreshToken();
    if (!refreshToken) {
      this.clearSession();
      return throwError(() => new Error('No refresh token available'));
    }

    const body: RefreshRequest = { refreshToken };
    this.refreshInFlight = this.http.post<AuthResponse>(REFRESH_PATH, body).pipe(
      tap((res) => this.persist(res)),
      map((res) => res.token),
      catchError((err) => {
        // Unknown/expired/already-rotated token: the session is unrecoverable.
        this.clearSession();
        return throwError(() => err);
      }),
      // Clearing the slot on completion means the *next* 401 starts a fresh refresh, while
      // everyone who joined this one still gets its replayed result.
      finalize(() => {
        this.refreshInFlight = null;
      }),
      shareReplay({ bufferSize: 1, refCount: false }),
    );

    return this.refreshInFlight;
  }

  /**
   * Best-effort server-side revocation of every refresh token this user holds, then local
   * teardown. The local session is cleared even when the call fails — a user who clicked
   * "log out" must end up logged out of this browser regardless of the network.
   */
  async logout(): Promise<void> {
    if (readAccessToken()) {
      await firstValueFrom(
        this.http.post<{ revoked: number }>(LOGOUT_PATH, {}).pipe(catchError(() => of(null))),
      );
    }
    this.clearSession();
    await this.router.navigate(['/login']);
  }

  /** Drops the local session without touching the server or navigating. */
  clearSession(): void {
    clearSessionStorage();
    this.currentUser.set(null);
    this.refreshInFlight = null;
  }

  /** Restores the session from localStorage on app bootstrap (e.g. after a page refresh). */
  loadFromStorage(): void {
    const token = readAccessToken();
    if (!token) {
      return;
    }
    const user = readUser();
    if (!user) {
      this.clearSession();
      return;
    }
    this.currentUser.set(user);
  }

  private persist(res: AuthResponse): void {
    writeSession(res.token, res.refreshToken ?? null, res.user);
    this.currentUser.set(res.user);
  }
}
