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
  Agent,
  AgentVersion,
  Approval,
  AuditEntryView,
  AuditFacets,
  AuditLogEntry,
  AuditPage,
  ManifestValidationError,
  ManifestValidationFailure,
  ManifestValidationSuccess,
  Organization,
  ParsedManifest,
  Project,
  ProjectMemory,
  Run,
  RunEvent,
  RunStatus,
  Secret,
  User,
  UserRole,
  Webhook,
} from '../models';
import { JsonObject, JsonValue } from '../manifest';

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

export function project(overrides: Partial<Project> = {}): Project {
  return {
    id: 'p1',
    orgId: 'o1',
    name: 'Site vitrine',
    slug: 'site-vitrine',
    description: null,
    budgetMonthlyUsd: 1000,
    createdAt: T0,
    updatedAt: T0,
    ...overrides,
  };
}

export function agent(overrides: Partial<Agent> = {}): Agent {
  return {
    id: 'ag1',
    orgId: 'o1',
    projectId: 'p1',
    name: 'Rédacteur',
    slug: 'redacteur',
    agentType: 'oci',
    imageRef: null,
    manifestYaml: 'name: redacteur\nversion: 1',
    inputsSchema: { type: 'object', properties: {} },
    outputsSchema: null,
    currentVersionId: 'v1',
    isPublished: true,
    createdAt: T0,
    updatedAt: T0,
    ...overrides,
  };
}

export function agentVersion(overrides: Partial<AgentVersion> = {}): AgentVersion {
  return {
    id: 'v1',
    agentId: 'ag1',
    versionNumber: 1,
    manifestYaml: 'name: redacteur\nversion: 1',
    imageRef: null,
    inputsSchema: '{"type":"object"}',
    outputsSchema: null,
    digestSha256: 'sha256:1111111111111111111111111111111111111111111111111111111111111111',
    createdAt: T0,
    ...overrides,
  };
}

/**
 * `status` est en premier paramètre car c'est ce que la plupart des tests font varier —
 * même convention que `user(role, overrides)`.
 */
export function run(status: RunStatus = 'succeeded', overrides: Partial<Run> = {}): Run {
  return {
    id: 'r1',
    orgId: 'o1',
    projectId: 'p1',
    number: 1,
    agentId: 'ag1',
    agentVersionId: 'v1',
    status,
    inputs: {},
    context: {},
    outputs: null,
    durationMs: null,
    exitCode: null,
    errorMessage: null,
    errorCode: null,
    budgetMaxUsd: null,
    budgetUsedUsd: null,
    triggeredByUserId: 'u1',
    triggeredByType: 'manual',
    parentRunId: null,
    rootRunId: null,
    createdAt: T0,
    startedAt: null,
    finishedAt: null,
    updatedAt: T0,
    ...overrides,
  };
}

/**
 * `seq` en premier paramètre : c'est l'ordre des évènements que les tests temps réel font varier,
 * et le hub SignalR les pousse un par un.
 */
export function runEvent(seq = 1, overrides: Partial<RunEvent> = {}): RunEvent {
  return {
    runId: 'r1',
    seq,
    timestamp: T0,
    eventType: 'log',
    level: 'info',
    message: `event ${seq}`,
    payload: null,
    ...overrides,
  };
}

/**
 * `requiredRole` en premier paramètre : le masquage du bouton de décision en dépend directement.
 * `null` signifie « aucun rôle minimal imposé par l'agent ».
 */
export function approval(
  requiredRole: UserRole | null = 'maintainer',
  overrides: Partial<Approval> = {},
): Approval {
  return {
    id: 'ap1',
    runId: 'r1',
    stepId: null,
    approvalType: 'gate',
    prompt: 'Déployer en production ?',
    options: null,
    requiredRole,
    requiredCount: 1,
    responses: [],
    status: 'pending',
    expiresAt: '2026-12-31T00:00:00Z',
    decidedAt: null,
    decidedBy: null,
    createdAt: T0,
    ...overrides,
  };
}

export function projectMemory(overrides: Partial<ProjectMemory> = {}): ProjectMemory {
  return {
    projectId: 'p1',
    context: {
      name: 'Site vitrine',
      description: 'Vitrine publique',
      technologies: ['Angular'],
      recentDecisions: [],
    },
    runHistory: [],
    patterns: [],
    learnings: [],
    notes: [],
    archived: [],
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

/**
 * Une entrée telle que la consultation filtrée la sert : sans `orgId` (le périmètre vient du
 * jeton) et avec l'acteur déjà résolu par le serveur.
 */
export function auditEntryView(overrides: Partial<AuditEntryView> = {}): AuditEntryView {
  return {
    id: 'a1',
    action: 'run.created',
    actorUserId: 'u1',
    actorEmail: 'alice@example.com',
    actorDisplayName: 'Alice',
    resourceType: 'run',
    resourceId: 'r1',
    changes: null,
    details: null,
    createdAt: T0,
    ...overrides,
  };
}

export function auditFacets(overrides: Partial<AuditFacets> = {}): AuditFacets {
  return {
    actions: [
      { value: 'run.created', count: 3 },
      { value: 'secret.rotated', count: 1 },
    ],
    resourceTypes: [{ value: 'run', count: 3 }],
    actors: [{ userId: 'u1', email: 'alice@example.com', displayName: 'Alice', count: 4 }],
    earliestEntry: T0,
    totalEntries: 4,
    ...overrides,
  };
}

/** Une page de journal, avec un total cohérent par défaut. */
export function auditPage(overrides: Partial<AuditPage> = {}): AuditPage {
  const items = overrides.items ?? [auditEntryView()];
  return {
    items,
    total: items.length,
    skip: 0,
    take: 50,
    ...overrides,
  };
}

/**
 * Rejoue côté test ce que fait `POST /api/agents/validate-manifest` sur un document déjà projeté
 * en JSON, sans serveur.
 *
 * Deux comportements du backend sont reproduits ici, et un seul est anodin :
 *  - les défauts du parseur (`displayName` retombe sur `name`, `cpu` vaut 2, `budget` 5/10, ...) ;
 *  - l'écrasement en chaîne de tout scalaire des arbres libres `spec.inputs` / `spec.outputs`,
 *    parce que le parseur les désérialise en `object`. C'est précisément la perte que le document
 *    brut évite, et la reproduire ici garantit que le convertisseur ne s'appuie jamais dessus.
 *
 * Ces deux comportements sont vérifiés contre le vrai parseur par
 * `backend/tests/.../ManifestValidationEndpointTests` et
 * `CanonicalManifestContractTests`, sur le même manifeste que `nonTrivialManifestDocument()`.
 */
export function manifestValidation(document: JsonObject): ManifestValidationSuccess {
  const metadata = (document['metadata'] ?? {}) as JsonObject;
  const spec = (document['spec'] ?? {}) as JsonObject;
  const permissions = (spec['permissions'] ?? {}) as JsonObject;
  const runtime = (spec['runtime'] ?? {}) as JsonObject;
  const budget = (spec['budget'] ?? {}) as JsonObject;
  const external = spec['external'] as JsonObject | undefined;
  const approvals = spec['approvals'] as JsonObject | undefined;
  const beforeWrite = approvals?.['beforeWrite'] as JsonObject | undefined;
  const name = (metadata['name'] as string | undefined) ?? '';

  return {
    valid: true,
    manifest: {
      apiVersion: (document['apiVersion'] as string | undefined) ?? 'agenthost.dev/v1',
      kind: (document['kind'] as string | undefined) ?? 'Agent',
      metadata: {
        name,
        displayName: (metadata['displayName'] as string | undefined) ?? name,
        description: (metadata['description'] as string | undefined) ?? '',
      },
      spec: {
        type: ((spec['type'] as string | undefined) ?? 'oci') as ParsedManifest['spec']['type'],
        image: (spec['image'] as string | undefined) ?? null,
        external: external
          ? {
              provider: (external['provider'] as string | undefined) ?? '',
              model: (external['model'] as string | undefined) ?? null,
              config: Object.fromEntries(
                Object.entries((external['config'] ?? {}) as JsonObject).map(([k, v]) => [
                  k,
                  String(v),
                ]),
              ),
            }
          : null,
        inputs: (stringifyScalars(spec['inputs'] ?? {}) ?? {}) as Record<string, unknown>,
        outputs:
          spec['outputs'] === undefined
            ? null
            : (stringifyScalars(spec['outputs']) as Record<string, unknown>),
        permissions: {
          vcs: (permissions['vcs'] as string | undefined) ?? 'none',
          network: (permissions['network'] as string | undefined) ?? 'none',
          secrets: (permissions['secrets'] as string[] | undefined) ?? [],
          docker: (permissions['docker'] as boolean | undefined) ?? false,
        },
        runtime: {
          profile: (runtime['profile'] as string | undefined) ?? 'standard',
          cpu: (runtime['cpu'] as number | undefined) ?? 2,
          memory: (runtime['memory'] as string | undefined) ?? '2Gi',
          disk: (runtime['disk'] as string | undefined) ?? '10Gi',
          maxDurationSeconds: (runtime['maxDurationSeconds'] as number | undefined) ?? 3600,
        },
        budget: {
          defaultMaxUsd: (budget['defaultMaxUsd'] as number | undefined) ?? 5,
          hardMaxUsd: (budget['hardMaxUsd'] as number | undefined) ?? 10,
        },
        approvals: beforeWrite
          ? {
              beforeWrite: {
                requiredRole: (beforeWrite['requiredRole'] as string | undefined) ?? 'maintainer',
                requiredCount: (beforeWrite['requiredCount'] as number | undefined) ?? 1,
              },
            }
          : null,
      },
    },
    permissionExtensions: {
      networkAllowlist: (permissions['networkAllowlist'] as string[] | undefined) ?? [],
      writableRootfs: (permissions['writableRootfs'] as boolean | undefined) ?? false,
    },
    document,
    error: null,
  };
}

export function manifestValidationFailure(
  overrides: Partial<ManifestValidationError> = {},
): ManifestValidationFailure {
  return {
    valid: false,
    manifest: null,
    permissionExtensions: null,
    document: null,
    error: { message: 'Failed to parse agent manifest YAML', line: 4, column: 3, ...overrides },
  };
}

function stringifyScalars(value: JsonValue): JsonValue {
  if (Array.isArray(value)) return value.map(stringifyScalars);
  if (typeof value === 'object' && value !== null) {
    return Object.fromEntries(Object.entries(value).map(([k, v]) => [k, stringifyScalars(v)]));
  }
  return value === null ? '' : String(value);
}

/**
 * Un manifeste non trivial : entrées à plusieurs champs et plusieurs types, sortie, allowlist
 * réseau, secrets, rootfs inscriptible, fournisseur externe avec configuration, porte
 * d'approbation. C'est le sujet du test d'aller-retour.
 */
export function nonTrivialManifestDocument(): JsonObject {
  return {
    apiVersion: 'agenthost.dev/v1',
    kind: 'Agent',
    metadata: {
      name: 'redacteur',
      displayName: 'Rédacteur de release notes',
      description: 'Rédige les notes de version à partir des commits',
    },
    spec: {
      type: 'claude_code',
      image: 'ghcr.io/acme/redacteur:1.4.0',
      external: {
        provider: 'claude_code',
        model: 'claude-opus-4',
        config: { temperature: '0.2', maxTurns: '8' },
      },
      inputs: {
        type: 'object',
        properties: {
          repository: {
            type: 'string',
            title: 'Dépôt',
            description: 'Dépôt à analyser',
          },
          sinceTag: { type: 'string', description: 'Tag de départ', default: 'v1.0.0' },
          maxCommits: { type: 'integer', description: 'Nombre de commits max', default: 200 },
          temperature: { type: 'number', default: 0.2 },
          draft: { type: 'boolean', description: 'Publier en brouillon', default: true },
          tone: { type: 'string', description: 'Ton', enum: ['neutre', 'commercial'], default: 'neutre' },
        },
        required: ['repository', 'sinceTag'],
      },
      outputs: {
        type: 'object',
        properties: { markdown: { type: 'string', description: 'Notes rédigées' } },
        required: ['markdown'],
      },
      permissions: {
        vcs: 'write_pr',
        network: 'allowlist',
        networkAllowlist: ['api.github.com', 'registry.npmjs.org'],
        secrets: ['GITHUB_TOKEN', 'ANTHROPIC_API_KEY'],
        docker: true,
        writableRootfs: true,
      },
      runtime: {
        profile: 'large',
        cpu: 4,
        memory: '8Gi',
        disk: '20Gi',
        maxDurationSeconds: 1800,
      },
      budget: { defaultMaxUsd: 2.5, hardMaxUsd: 7.5 },
      approvals: { beforeWrite: { requiredRole: 'maintainer', requiredCount: 2 } },
    },
  };
}
