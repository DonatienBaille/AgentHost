import {
  manifestValidation,
  nonTrivialManifestDocument,
} from '../testing/fixtures';
import { JsonObject } from './json-value';
import { emptyManifestModel } from './manifest-form.model';
import { importManifest, manifestModelToDocument, manifestModelToYaml } from './manifest-codec';

/** Le tour complet : document → modèle → document, comme la bascule le fait. */
function reimport(document: JsonObject) {
  return importManifest(manifestValidation(document));
}

describe('manifest codec', () => {
  describe('round trip on a non-trivial manifest', () => {
    it('loses nothing between form, YAML and form again', () => {
      const first = reimport(nonTrivialManifestDocument());
      expect(first.unsupported).toEqual([]);

      // form -> YAML (le document émis est ce que `manifestModelToYaml` sérialise)
      const emitted = manifestModelToDocument(first.model);
      // YAML -> form
      const second = reimport(emitted);

      expect(second.model).toEqual(first.model);
      expect(second.unsupported).toEqual([]);
      // Et le YAML est un point fixe : rééditer sans rien changer ne réécrit pas le fichier.
      expect(manifestModelToYaml(second.model)).toBe(manifestModelToYaml(first.model));
    });

    it('keeps every input field with its type, order, default and required flag', () => {
      const { model } = reimport(nonTrivialManifestDocument());

      expect(model.inputs.editable).toBe(true);
      expect(model.inputs.fields.map((f) => f.name)).toEqual([
        'repository',
        'sinceTag',
        'maxCommits',
        'temperature',
        'draft',
        'tone',
      ]);
      expect(model.inputs.fields.map((f) => f.type)).toEqual([
        'string',
        'string',
        'integer',
        'number',
        'boolean',
        'string',
      ]);
      expect(model.inputs.fields.map((f) => f.required)).toEqual([
        true,
        true,
        false,
        false,
        false,
        false,
      ]);
      expect(model.inputs.fields[0].title).toBe('Dépôt');
      expect(model.inputs.fields[5].enumValues).toEqual(['neutre', 'commercial']);
    });

    it('keeps a numeric default a number rather than the string the typed parser would give', () => {
      const { model } = reimport(nonTrivialManifestDocument());

      const maxCommits = model.inputs.fields.find((f) => f.name === 'maxCommits')!;
      expect(maxCommits.defaultValue).toBe(200);
      const temperature = model.inputs.fields.find((f) => f.name === 'temperature')!;
      expect(temperature.defaultValue).toBe(0.2);
      const draft = model.inputs.fields.find((f) => f.name === 'draft')!;
      expect(draft.defaultValue).toBe(true);
    });

    it('keeps the parts of the manifest that live outside the typed contract', () => {
      const { model } = reimport(nonTrivialManifestDocument());

      // networkAllowlist et writableRootfs ne sont pas dans AgentManifest : ils arrivent par
      // permissionExtensions et doivent ressortir dans le YAML.
      expect(model.networkAllowlist).toEqual(['api.github.com', 'registry.npmjs.org']);
      expect(model.writableRootfs).toBe(true);
      expect(model.secrets).toEqual(['GITHUB_TOKEN', 'ANTHROPIC_API_KEY']);

      const emitted = manifestModelToDocument(model);
      const permissions = (emitted['spec'] as JsonObject)['permissions'] as JsonObject;
      expect(permissions['networkAllowlist']).toEqual(['api.github.com', 'registry.npmjs.org']);
      expect(permissions['writableRootfs']).toBe(true);
      expect(permissions['docker']).toBe(true);
    });

    it('keeps the approval gate and the external provider config', () => {
      const { model } = reimport(nonTrivialManifestDocument());

      expect(model.approvalsEnabled).toBe(true);
      expect(model.approvalRole).toBe('maintainer');
      expect(model.approvalCount).toBe(2);
      expect(model.externalEnabled).toBe(true);
      expect(model.externalConfig).toEqual([
        { key: 'temperature', value: '0.2' },
        { key: 'maxTurns', value: '8' },
      ]);
    });
  });

  describe('constructs the form cannot edit', () => {
    it('names an unknown key and carries it through untouched', () => {
      const document = nonTrivialManifestDocument();
      (document['spec'] as JsonObject)['sidecars'] = [{ image: 'redis:7', ports: [6379] }];

      const { model, unsupported } = reimport(document);

      expect(unsupported).toEqual([{ path: 'spec.sidecars', reason: 'unknownKey' }]);
      const emitted = manifestModelToDocument(model);
      expect((emitted['spec'] as JsonObject)['sidecars']).toEqual([
        { image: 'redis:7', ports: [6379] },
      ]);
      // Et la seconde lecture le signale encore : l'avertissement ne s'efface pas tout seul.
      expect(reimport(emitted).unsupported).toEqual([{ path: 'spec.sidecars', reason: 'unknownKey' }]);
    });

    it('names an unknown key nested under a section the form does model', () => {
      const document = nonTrivialManifestDocument();
      (document['metadata'] as JsonObject)['labels'] = { team: 'platform' };
      ((document['spec'] as JsonObject)['runtime'] as JsonObject)['gpu'] = 1;

      const { model, unsupported } = reimport(document);

      expect(unsupported).toEqual([
        { path: 'metadata.labels', reason: 'unknownKey' },
        { path: 'spec.runtime.gpu', reason: 'unknownKey' },
      ]);
      const emitted = manifestModelToDocument(model);
      expect((emitted['metadata'] as JsonObject)['labels']).toEqual({ team: 'platform' });
      expect(((emitted['spec'] as JsonObject)['runtime'] as JsonObject)['gpu']).toBe(1);
    });

    it('refuses to edit a schema with a keyword it does not know, and keeps it whole', () => {
      const document = nonTrivialManifestDocument();
      const inputs = (document['spec'] as JsonObject)['inputs'] as JsonObject;
      (inputs['properties'] as JsonObject)['payload'] = {
        oneOf: [{ type: 'string' }, { type: 'integer' }],
      };

      const { model, unsupported } = reimport(document);

      expect(model.inputs.editable).toBe(false);
      expect(model.inputs.fields).toEqual([]);
      expect(unsupported).toContainEqual({
        path: 'spec.inputs.properties.payload.oneOf',
        reason: 'schemaPropertyKeyword',
      });
      // Le schéma entier ressort tel quel, les six autres propriétés comprises.
      const emitted = manifestModelToDocument(model);
      expect((emitted['spec'] as JsonObject)['inputs']).toEqual(inputs);
    });

    it('refuses a property type the run form could not render', () => {
      const document = nonTrivialManifestDocument();
      const inputs = (document['spec'] as JsonObject)['inputs'] as JsonObject;
      (inputs['properties'] as JsonObject)['files'] = { type: 'array' };

      const { model, unsupported } = reimport(document);

      expect(model.inputs.editable).toBe(false);
      expect(unsupported).toContainEqual({
        path: 'spec.inputs.properties.files.type',
        reason: 'schemaPropertyType',
      });
    });

    it('refuses a root keyword outside type/properties/required', () => {
      const document = nonTrivialManifestDocument();
      const inputs = (document['spec'] as JsonObject)['inputs'] as JsonObject;
      inputs['additionalProperties'] = false;

      const { model, unsupported } = reimport(document);

      expect(model.inputs.editable).toBe(false);
      expect(unsupported).toContainEqual({
        path: 'spec.inputs.additionalProperties',
        reason: 'schemaRootKeyword',
      });
      expect(manifestModelToDocument(model)['spec']).toHaveProperty('inputs', inputs);
    });

    it('refuses a default whose text form would not read back the same', () => {
      const document = nonTrivialManifestDocument();
      const inputs = (document['spec'] as JsonObject)['inputs'] as JsonObject;
      // La chaîne "7" se relirait en nombre 7 : le champ texte mentirait sur ce qu'il édite.
      (inputs['properties'] as JsonObject)['version'] = { type: 'string', default: '7' };

      const { model, unsupported } = reimport(document);

      expect(model.inputs.editable).toBe(false);
      expect(unsupported).toContainEqual({
        path: 'spec.inputs.properties.version.default',
        reason: 'schemaValue',
      });
    });

    it('refuses a required entry that names no property', () => {
      const document = nonTrivialManifestDocument();
      const inputs = (document['spec'] as JsonObject)['inputs'] as JsonObject;
      inputs['required'] = ['repository', 'ghost'];

      const { model, unsupported } = reimport(document);

      expect(model.inputs.editable).toBe(false);
      expect(unsupported).toContainEqual({
        path: 'spec.inputs.required.ghost',
        reason: 'schemaShape',
      });
    });

    it('reports a non-editable outputs schema separately from inputs', () => {
      const document = nonTrivialManifestDocument();
      (document['spec'] as JsonObject)['outputs'] = { $ref: '#/definitions/notes' };

      const { model, unsupported } = reimport(document);

      expect(model.inputs.editable).toBe(true);
      expect(model.outputs.editable).toBe(false);
      expect(unsupported).toEqual([{ path: 'spec.outputs.$ref', reason: 'schemaRootKeyword' }]);
      expect(manifestModelToDocument(model)['spec']).toHaveProperty('outputs', {
        $ref: '#/definitions/notes',
      });
    });

    it('round-trips a manifest that is unsupported through and through', () => {
      const document = nonTrivialManifestDocument();
      (document['spec'] as JsonObject)['sidecars'] = ['redis'];
      ((document['spec'] as JsonObject)['inputs'] as JsonObject)['additionalProperties'] = false;
      (document['spec'] as JsonObject)['outputs'] = { $ref: '#/x' };

      const first = reimport(document);
      const second = reimport(manifestModelToDocument(first.model));

      expect(second.model).toEqual(first.model);
      expect(second.unsupported).toEqual(first.unsupported);
    });
  });

  describe('emitted YAML', () => {
    it('writes the canonical document for the non-trivial manifest', () => {
      const { model } = reimport(nonTrivialManifestDocument());

      expect(manifestModelToYaml(model)).toBe(CANONICAL_YAML);
    });

    it('omits what is empty rather than writing empty keys', () => {
      const model = emptyManifestModel();
      model.name = 'minimal';

      const yaml = manifestModelToYaml(model);

      expect(yaml).not.toContain('image');
      expect(yaml).not.toContain('external');
      expect(yaml).not.toContain('outputs');
      expect(yaml).not.toContain('approvals');
      expect(yaml).not.toContain('secrets');
      expect(yaml).not.toContain('displayName');
      // vcs et network sont toujours écrits, même à leur défaut : ce sont les deux décisions de
      // sécurité du manifeste.
      expect(yaml).toContain('vcs: none');
      expect(yaml).toContain('network: none');
    });

    it('re-reads its own output for an empty manifest', () => {
      const model = emptyManifestModel();
      model.name = 'minimal';

      const first = reimport(manifestModelToDocument(model));
      const second = reimport(manifestModelToDocument(first.model));

      expect(second.model).toEqual(first.model);
      expect(second.unsupported).toEqual([]);
    });

    it('does not fill an empty display name with the parser fallback', () => {
      const model = emptyManifestModel();
      model.name = 'minimal';

      // Le parseur renvoie displayName = name quand la clé manque ; le formulaire doit rester vide,
      // sinon un champ qu'on a laissé tranquille se remplit tout seul en changeant d'onglet.
      const first = reimport(manifestModelToDocument(model));

      expect(first.model.displayName).toBe('');
      expect(manifestModelToYaml(first.model)).toBe(manifestModelToYaml(model));
    });
  });
});

/**
 * Le YAML canonique du manifeste non trivial. Le même texte est rejoué contre le vrai parseur par
 * `backend/tests/AgentHost.Api.Tests/Integration/CanonicalManifestContractTests.cs` : c'est ce qui
 * relie cette attente au serveur plutôt qu'à une fabrique de test.
 */
const CANONICAL_YAML = `apiVersion: agenthost.dev/v1
kind: Agent
metadata:
  name: redacteur
  displayName: Rédacteur de release notes
  description: Rédige les notes de version à partir des commits
spec:
  type: claude_code
  image: ghcr.io/acme/redacteur:1.4.0
  external:
    provider: claude_code
    model: claude-opus-4
    config:
      temperature: "0.2"
      maxTurns: "8"
  inputs:
    type: object
    properties:
      repository:
        type: string
        title: Dépôt
        description: Dépôt à analyser
      sinceTag:
        type: string
        description: Tag de départ
        default: v1.0.0
      maxCommits:
        type: integer
        description: Nombre de commits max
        default: 200
      temperature:
        type: number
        default: 0.2
      draft:
        type: boolean
        description: Publier en brouillon
        default: true
      tone:
        type: string
        description: Ton
        default: neutre
        enum:
        - neutre
        - commercial
    required:
    - repository
    - sinceTag
  outputs:
    type: object
    properties:
      markdown:
        type: string
        description: Notes rédigées
    required:
    - markdown
  permissions:
    vcs: write_pr
    network: allowlist
    networkAllowlist:
    - api.github.com
    - registry.npmjs.org
    secrets:
    - GITHUB_TOKEN
    - ANTHROPIC_API_KEY
    docker: true
    writableRootfs: true
  runtime:
    profile: large
    cpu: 4
    memory: 8Gi
    disk: 20Gi
    maxDurationSeconds: 1800
  budget:
    defaultMaxUsd: 2.5
    hardMaxUsd: 7.5
  approvals:
    beforeWrite:
      requiredRole: maintainer
      requiredCount: 2
`;
