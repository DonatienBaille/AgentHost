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

export interface AuthResponse {
  token: string;
  user: User;
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

export interface CreateUserRequest {
  orgId: string;
  email: string;
  password: string;
  displayName?: string;
  role: UserRole;
}
