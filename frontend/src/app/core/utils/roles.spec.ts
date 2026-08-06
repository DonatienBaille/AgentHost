import { hasRoleAtLeast } from './roles';
import { UserRole } from '../models';

const ROLES: UserRole[] = ['viewer', 'developer', 'maintainer', 'owner'];

describe('hasRoleAtLeast', () => {
  it('is true exactly when the actor ranks at or above the minimum', () => {
    // owner > maintainer > developer > viewer, checked for every pair.
    ROLES.forEach((actor, actorRank) => {
      ROLES.forEach((min, minRank) => {
        expect({ actor, min, allowed: hasRoleAtLeast(actor, min) }).toEqual({
          actor,
          min,
          allowed: actorRank >= minRank,
        });
      });
    });
  });

  it('every role satisfies its own minimum (boundary)', () => {
    for (const role of ROLES) {
      expect(hasRoleAtLeast(role, role)).toBe(true);
    }
  });

  it('viewer is the floor — it satisfies only viewer', () => {
    expect(hasRoleAtLeast('viewer', 'viewer')).toBe(true);
    expect(hasRoleAtLeast('viewer', 'developer')).toBe(false);
    expect(hasRoleAtLeast('viewer', 'maintainer')).toBe(false);
    expect(hasRoleAtLeast('viewer', 'owner')).toBe(false);
  });

  it('owner is the ceiling — it satisfies every minimum', () => {
    for (const min of ROLES) {
      expect(hasRoleAtLeast('owner', min)).toBe(true);
    }
  });

  it('an absent role is never sufficient', () => {
    for (const min of ROLES) {
      expect(hasRoleAtLeast(null, min)).toBe(false);
      expect(hasRoleAtLeast(undefined, min)).toBe(false);
    }
  });
});
