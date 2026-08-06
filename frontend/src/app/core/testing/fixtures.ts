/**
 * Fabriques partagées pour les tests d'IHM.
 *
 * Chaque fabrique renvoie un objet complet et valide au regard du contrat de `core/models`, puis
 * applique les surcharges fournies. Les objets sont donc alignés sur la forme réelle du contrat :
 * si un champ disparaît du modèle, la compilation des tests casse ici, en un seul endroit.
 *
 * Les dates sont fixes et déterministes — aucune assertion ne doit dépendre de l'horloge.
 */
import {
  AuditLogEntry,
  Organization,
  Secret,
  User,
  UserRole,
  Webhook,
} from '../models';

const T0 = '2026-01-01T00:00:00Z';

export function user(role: UserRole = 'owner', overrides: Partial<User> = {}): User {
  return {
    id: 'u1',
    orgId: 'o1',
    email: 'someone@example.com',
    displayName: null,
    avatarUrl: null,
    role,
    createdAt: T0,
    updatedAt: T0,
    ...overrides,
  };
}

export function organization(overrides: Partial<Organization> = {}): Organization {
  return {
    id: 'o1',
    name: 'Acme',
    slug: 'acme',
    plan: 'free',
    createdAt: T0,
    updatedAt: T0,
    ...overrides,
  };
}

export function secret(overrides: Partial<Secret> = {}): Secret {
  return {
    id: 's1',
    orgId: 'o1',
    projectId: null,
    name: 'OPENAI_API_KEY',
    scope: 'org',
    lastUsedAt: null,
    lastUsedByRunId: null,
    createdAt: T0,
    updatedAt: T0,
    ...overrides,
  };
}

export function webhook(overrides: Partial<Webhook> = {}): Webhook {
  return {
    id: 'w1',
    projectId: 'p1',
    url: 'https://example.com/hooks/agenthost',
    events: ['run.succeeded'],
    secretToken: null,
    isActive: true,
    createdAt: T0,
    updatedAt: T0,
    ...overrides,
  };
}

export function auditEntry(overrides: Partial<AuditLogEntry> = {}): AuditLogEntry {
  return {
    id: 'a1',
    orgId: 'o1',
    action: 'run.created',
    actorUserId: 'u1',
    resourceType: 'run',
    resourceId: 'r1',
    changes: null,
    details: null,
    createdAt: T0,
    ...overrides,
  };
}
