import { User } from '../models';

/**
 * Single source of truth for the localStorage keys holding the session.
 *
 * These used to be duplicated as string literals in auth.service.ts and auth.interceptor.ts;
 * the refresh flow adds a third reader (the error interceptor) and a fourth (SignalR), so the
 * keys and the (de)serialisation live here instead.
 */
export const TOKEN_KEY = 'agenthost_token';
export const REFRESH_TOKEN_KEY = 'agenthost_refresh_token';
export const USER_KEY = 'agenthost_user';

export function readAccessToken(): string | null {
  return safeGet(TOKEN_KEY);
}

export function readRefreshToken(): string | null {
  return safeGet(REFRESH_TOKEN_KEY);
}

export function readUser(): User | null {
  const raw = safeGet(USER_KEY);
  if (!raw) return null;
  try {
    return JSON.parse(raw) as User;
  } catch {
    return null;
  }
}

export function writeSession(token: string, refreshToken: string | null, user: User): void {
  safeSet(TOKEN_KEY, token);
  if (refreshToken) {
    safeSet(REFRESH_TOKEN_KEY, refreshToken);
  } else {
    safeRemove(REFRESH_TOKEN_KEY);
  }
  safeSet(USER_KEY, JSON.stringify(user));
}

export function clearSessionStorage(): void {
  safeRemove(TOKEN_KEY);
  safeRemove(REFRESH_TOKEN_KEY);
  safeRemove(USER_KEY);
}

function safeGet(key: string): string | null {
  try {
    return typeof localStorage !== 'undefined' ? localStorage.getItem(key) : null;
  } catch {
    return null;
  }
}

function safeSet(key: string, value: string): void {
  try {
    localStorage?.setItem(key, value);
  } catch {
    // Storage unavailable (private mode, SSR): the session simply won't survive a reload.
  }
}

function safeRemove(key: string): void {
  try {
    localStorage?.removeItem(key);
  } catch {
    // see safeSet
  }
}
