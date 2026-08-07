import { AgentType } from '../models/agent.model';
import { ManifestValidationSuccess } from '../models/manifest-validation.model';
import { JsonObject, JsonValue, isJsonObject } from './json-value';
import {
  ManifestConfigEntry,
  ManifestFormModel,
  PreservedFragment,
  UnsupportedConstruct,
  emptyManifestModel,
} from './manifest-form.model';
import { importSchemaSection, schemaSectionToJson } from './manifest-schema';
import { emitYaml } from './yaml-emitter';

/**
 * Clés que le formulaire modélise, niveau par niveau. Tout ce qui n'y figure pas est conservé tel
 * quel et signalé : c'est la moitié « ne jamais tronquer en silence » du contrat.
 *
 * Le parseur backend, lui, ignore les clés inconnues (`IgnoreUnmatchedProperties`). Il ne peut donc
 * pas répondre à la question posée ici — d'où le document brut renvoyé à côté du manifeste typé.
 */
const MODELLED_KEYS: Record<string, readonly string[]> = {
  '': ['apiVersion', 'kind', 'metadata', 'spec'],
  metadata: ['name', 'displayName', 'description'],
  spec: ['type', 'image', 'external', 'inputs', 'outputs', 'permissions', 'runtime', 'budget', 'approvals'],
  'spec.external': ['provider', 'model', 'config'],
  'spec.permissions': ['vcs', 'network', 'secrets', 'docker', 'networkAllowlist', 'writableRootfs'],
  'spec.runtime': ['profile', 'cpu', 'memory', 'disk', 'maxDurationSeconds'],
  'spec.budget': ['defaultMaxUsd', 'hardMaxUsd'],
  'spec.approvals': ['beforeWrite'],
  'spec.approvals.beforeWrite': ['requiredRole', 'requiredCount'],
};

export interface ManifestImport {
  model: ManifestFormModel;
  /** Ce que le mode UI ne sait pas éditer. Vide = la bascule ne coûte rien. */
  unsupported: UnsupportedConstruct[];
}

/**
 * YAML → modèle de formulaire, à partir de la réponse du serveur.
 *
 * Les champs de forme fixe viennent du manifeste **typé** : c'est le parseur qui fait autorité sur
 * les défauts (`displayName` retombe sur `name`, `cpu` vaut 2, ...) et les redériver ici serait
 * s'en écarter au premier changement. Les arbres libres (`spec.inputs`, `spec.outputs`) viennent du
 * document **brut**, parce que le parseur typé écrase leurs scalaires en chaînes et qu'un
 * `default: 5` réémis en `default: "5"` n'est pas un aller-retour sans perte.
 */
export function importManifest(validation: ManifestValidationSuccess): ManifestImport {
  const document = isJsonObject(validation.document as JsonValue) ? (validation.document as JsonObject) : {};
  const manifest = validation.manifest;
  const spec = isJsonObject(document['spec']) ? document['spec'] : {};

  const metadata = isJsonObject(document['metadata']) ? document['metadata'] : {};

  const model = emptyManifestModel();
  model.apiVersion = manifest.apiVersion;
  model.kind = manifest.kind;
  model.name = manifest.metadata.name;
  // Le parseur fait retomber `displayName` sur `name` quand la clé est absente. Reprendre ce défaut
  // ici transformerait un champ laissé vide en champ rempli au premier aller-retour : le document
  // brut dit si l'utilisateur a écrit quelque chose, et c'est cela qu'on garde.
  model.displayName = metadata['displayName'] === undefined ? '' : manifest.metadata.displayName;
  model.description = manifest.metadata.description;
  model.type = manifest.spec.type as AgentType;
  model.image = manifest.spec.image ?? '';

  if (manifest.spec.external) {
    model.externalEnabled = true;
    model.externalProvider = manifest.spec.external.provider;
    model.externalModel = manifest.spec.external.model ?? '';
    model.externalConfig = Object.entries(manifest.spec.external.config ?? {}).map(
      ([key, value]): ManifestConfigEntry => ({ key, value }),
    );
  }

  const unsupported: UnsupportedConstruct[] = [];

  const inputs = importSchemaSection(spec['inputs'], 'spec.inputs');
  model.inputs = inputs.section;
  unsupported.push(...inputs.issues);

  const outputs = importSchemaSection(spec['outputs'], 'spec.outputs');
  model.outputs = outputs.section;
  unsupported.push(...outputs.issues);

  model.vcs = manifest.spec.permissions.vcs;
  model.network = manifest.spec.permissions.network;
  model.secrets = [...(manifest.spec.permissions.secrets ?? [])];
  model.docker = manifest.spec.permissions.docker;
  model.networkAllowlist = [...(validation.permissionExtensions?.networkAllowlist ?? [])];
  model.writableRootfs = validation.permissionExtensions?.writableRootfs ?? false;

  model.runtimeProfile = manifest.spec.runtime.profile;
  model.cpu = manifest.spec.runtime.cpu;
  model.memory = manifest.spec.runtime.memory;
  model.disk = manifest.spec.runtime.disk;
  model.maxDurationSeconds = manifest.spec.runtime.maxDurationSeconds;

  model.defaultMaxUsd = manifest.spec.budget.defaultMaxUsd;
  model.hardMaxUsd = manifest.spec.budget.hardMaxUsd;

  const gate = manifest.spec.approvals?.beforeWrite;
  if (gate) {
    model.approvalsEnabled = true;
    model.approvalRole = gate.requiredRole;
    model.approvalCount = gate.requiredCount;
  }

  const preserved: PreservedFragment[] = [];
  collectUnmodelled(document, [], preserved);
  model.preserved = preserved;
  unsupported.push(...preserved.map((fragment) => ({ path: fragment.path.join('.'), reason: 'unknownKey' as const })));

  return { model, unsupported };
}

function collectUnmodelled(node: JsonObject, path: string[], into: PreservedFragment[]): void {
  const modelled = MODELLED_KEYS[path.join('.')];
  if (!modelled) return;

  for (const [key, value] of Object.entries(node)) {
    const childPath = [...path, key];
    if (!modelled.includes(key)) {
      into.push({ path: childPath, value });
      continue;
    }
    if (isJsonObject(value)) collectUnmodelled(value, childPath, into);
  }
}

/** Modèle de formulaire → document JSON, dans l'ordre canonique du manifeste. */
export function manifestModelToDocument(model: ManifestFormModel): JsonObject {
  const metadata: JsonObject = { name: model.name };
  if (model.displayName) metadata['displayName'] = model.displayName;
  if (model.description) metadata['description'] = model.description;

  const spec: JsonObject = { type: model.type };
  if (model.image) spec['image'] = model.image;

  if (model.externalEnabled) {
    const external: JsonObject = { provider: model.externalProvider };
    if (model.externalModel) external['model'] = model.externalModel;
    const config: JsonObject = {};
    for (const entry of model.externalConfig) {
      if (entry.key) config[entry.key] = entry.value;
    }
    if (Object.keys(config).length > 0) external['config'] = config;
    spec['external'] = external;
  }

  const inputs = schemaSectionToJson(model.inputs);
  if (inputs !== null) spec['inputs'] = inputs;
  const outputs = schemaSectionToJson(model.outputs);
  if (outputs !== null) spec['outputs'] = outputs;

  // vcs et network sont toujours écrits : ce sont les deux décisions de sécurité du manifeste, et
  // un manifeste qui ne dit rien de son accès au dépôt ou au réseau se lit mal, même quand les
  // défauts sont les bons.
  const permissions: JsonObject = { vcs: model.vcs, network: model.network };
  const allowlist = model.networkAllowlist.filter((host) => host.length > 0);
  if (allowlist.length > 0) permissions['networkAllowlist'] = allowlist;
  const secrets = model.secrets.filter((name) => name.length > 0);
  if (secrets.length > 0) permissions['secrets'] = secrets;
  if (model.docker) permissions['docker'] = true;
  if (model.writableRootfs) permissions['writableRootfs'] = true;
  spec['permissions'] = permissions;

  spec['runtime'] = {
    profile: model.runtimeProfile,
    cpu: model.cpu,
    memory: model.memory,
    disk: model.disk,
    maxDurationSeconds: model.maxDurationSeconds,
  };

  spec['budget'] = { defaultMaxUsd: model.defaultMaxUsd, hardMaxUsd: model.hardMaxUsd };

  if (model.approvalsEnabled) {
    spec['approvals'] = {
      beforeWrite: { requiredRole: model.approvalRole, requiredCount: model.approvalCount },
    };
  }

  const document: JsonObject = {
    apiVersion: model.apiVersion,
    kind: model.kind,
    metadata,
    spec,
  };

  // Les fragments non modélisés reviennent en dernier à leur niveau : ils n'ont pas de place
  // canonique, mais ils ont une place.
  for (const fragment of model.preserved) setAtPath(document, fragment.path, fragment.value);

  return document;
}

export function manifestModelToYaml(model: ManifestFormModel): string {
  return emitYaml(manifestModelToDocument(model));
}

function setAtPath(root: JsonObject, path: string[], value: JsonValue): void {
  let node = root;
  for (let i = 0; i < path.length - 1; i++) {
    const next = node[path[i]];
    if (!isJsonObject(next)) node[path[i]] = {};
    node = node[path[i]] as JsonObject;
  }
  node[path[path.length - 1]] = value;
}
