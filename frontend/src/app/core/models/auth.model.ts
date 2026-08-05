export type UserRole = 'owner' | 'maintainer' | 'developer' | 'viewer';

export interface User {
  id: string;
  orgId: string;
  email: string;
  displayName: string | null;
  avatarUrl: string | null;
  role: UserRole;
  createdAt: string;
  updatedAt: string;
}

/**
 * Matches backend Contracts/AuthContracts.cs. The access token is short-lived
 * (`expiresInSeconds`, 15 min by default); `refreshToken` is the opaque, revocable half of the
 * pair and is rotated on every call to POST /api/auth/refresh.
 */
export interface AuthResponse {
  token: string;
  refreshToken: string;
  expiresInSeconds: number;
  user: User;
}

export interface RefreshRequest {
  refreshToken: string;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  orgName: string;
  orgSlug: string;
  email: string;
  password: string;
  displayName?: string;
}

/**
 * No orgId: the server creates the user inside the caller's own organization, read from the JWT
 * (backend Contracts/UserContracts.cs).
 */
export interface CreateUserRequest {
  email: string;
  password: string;
  displayName?: string;
  role: UserRole;
}
