import { UserRole } from '../models';

/** Role hierarchy per spec: owner > maintainer > developer > viewer, each including those below it. */
const ROLE_ORDER: Record<UserRole, number> = {
  viewer: 0,
  developer: 1,
  maintainer: 2,
  owner: 3,
};

export function hasRoleAtLeast(role: UserRole | null | undefined, min: UserRole): boolean {
  if (!role) return false;
  return ROLE_ORDER[role] >= ROLE_ORDER[min];
}
