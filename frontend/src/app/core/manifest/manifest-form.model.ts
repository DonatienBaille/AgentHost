import { AgentType } from '../models/agent.model';
import { JsonValue } from './json-value';

/** Types de champ que le constructeur d'entrées sait éditer — ceux que le formulaire d'exécution sait rendre. */
export type ManifestFieldType = 'string' | 'boolean' | 'integer' | 'number';

export const MANIFEST_FIELD_TYPES: readonly ManifestFieldType[] = [
  'string',
  'boolean',
  'integer',
  'number',
];

/** Une propriété du schéma JSON, telle que le constructeur de champs la manipule. */
export interface ManifestField {
  name: string;
  type: ManifestFieldType;
  title: string;
  description: string;
  required: boolean;
  /** `null` = pas de `default` dans le schéma. Sinon un scalaire stable (voir `isStableScalar`). */
  defaultValue: JsonValue | null;
  /** `null` = pas d'`enum`. Sinon la liste, non vide, de scalaires stables. */
  enumValues: JsonValue[] | null;
}

/**
 * Un arbre `spec.inputs` / `spec.outputs`.
 *
 * `editable: false` veut dire que le schéma sort du sous-ensemble que le constructeur couvre. Il est
 * alors conservé dans `raw` et réémis tel quel : l'utilisateur perd la possibilité de l'éditer au
 * formulaire, jamais son contenu.
 */
export interface ManifestSchemaSection {
  present: boolean;
  editable: boolean;
  fields: ManifestField[];
  raw: JsonValue | null;
}

export interface ManifestConfigEntry {
  key: string;
  value: string;
}

/** Une clé du document que le formulaire ne modélise pas, gardée pour être réémise à l'identique. */
export interface PreservedFragment {
  /** Chemin depuis la racine, p. ex. `['spec', 'sidecars']`. */
  path: string[];
  value: JsonValue;
}

export type UnsupportedReason =
  | 'unknownKey'
  | 'schemaShape'
  | 'schemaRootKeyword'
  | 'schemaPropertyKeyword'
  | 'schemaPropertyType'
  | 'schemaValue';

/** Une construction du manifeste que le mode UI ne sait pas éditer — nommée, jamais tue. */
export interface UnsupportedConstruct {
  /** Chemin pointé, tel qu'affiché à l'utilisateur : `spec.inputs.properties.payload`. */
  path: string;
  reason: UnsupportedReason;
}

/**
 * Projection éditable du manifeste.
 *
 * Ce n'est pas un format de stockage : rien de ceci n'est persisté. Le YAML reste la seule source de
 * vérité ; ce modèle n'existe qu'entre deux conversions.
 */
export interface ManifestFormModel {
  apiVersion: string;
  kind: string;
  name: string;
  displayName: string;
  description: string;

  type: AgentType;
  image: string;

  externalEnabled: boolean;
  externalProvider: string;
  externalModel: string;
  externalConfig: ManifestConfigEntry[];

  inputs: ManifestSchemaSection;
  outputs: ManifestSchemaSection;

  vcs: string;
  network: string;
  networkAllowlist: string[];
  secrets: string[];
  docker: boolean;
  writableRootfs: boolean;

  runtimeProfile: string;
  cpu: number;
  memory: string;
  disk: string;
  maxDurationSeconds: number;

  defaultMaxUsd: number;
  hardMaxUsd: number;

  approvalsEnabled: boolean;
  approvalRole: string;
  approvalCount: number;

  preserved: PreservedFragment[];
}

export const VCS_PERMISSIONS = ['none', 'read', 'write_branch', 'write_pr', 'push_default'] as const;
export const NETWORK_PERMISSIONS = ['none', 'allowlist', 'full'] as const;
export const AGENT_TYPES: readonly AgentType[] = ['oci', 'copilot', 'claude_code', 'openai', 'custom'];
export const RUNTIME_PROFILES = ['small', 'standard', 'large'] as const;
export const APPROVAL_ROLES = ['owner', 'maintainer', 'developer'] as const;

/** Le modèle d'un manifeste vide, aligné sur les défauts du parseur backend. */
export function emptyManifestModel(): ManifestFormModel {
  return {
    apiVersion: 'agenthost.dev/v1',
    kind: 'Agent',
    name: '',
    displayName: '',
    description: '',
    type: 'oci',
    image: '',
    externalEnabled: false,
    externalProvider: '',
    externalModel: '',
    externalConfig: [],
    inputs: { present: true, editable: true, fields: [], raw: null },
    outputs: { present: false, editable: true, fields: [], raw: null },
    vcs: 'none',
    network: 'none',
    networkAllowlist: [],
    secrets: [],
    docker: false,
    writableRootfs: false,
    runtimeProfile: 'standard',
    cpu: 2,
    memory: '2Gi',
    disk: '10Gi',
    maxDurationSeconds: 3600,
    defaultMaxUsd: 5,
    hardMaxUsd: 10,
    approvalsEnabled: false,
    approvalRole: 'maintainer',
    approvalCount: 1,
    preserved: [],
  };
}
