import { JsonObject, JsonValue, isJsonObject, isScalar, isStableScalar } from './json-value';
import {
  MANIFEST_FIELD_TYPES,
  ManifestField,
  ManifestFieldType,
  ManifestSchemaSection,
  UnsupportedConstruct,
} from './manifest-form.model';

/**
 * Mots-clés que le constructeur de champs sait éditer.
 *
 * Le sous-ensemble n'est pas choisi au hasard : c'est exactement ce que
 * `components/new-run-form` sait rendre à l'exécution (`core/models/json-schema.model.ts`).
 * Un schéma qui en sort décrit un formulaire d'exécution que la plateforme ne construit pas non
 * plus — le constructeur ne prétend donc pas couvrir plus que le reste du produit.
 */
const SCHEMA_ROOT_KEYWORDS = new Set(['type', 'properties', 'required']);
const PROPERTY_KEYWORDS = new Set(['type', 'title', 'description', 'default', 'enum']);

export interface SchemaImport {
  section: ManifestSchemaSection;
  issues: UnsupportedConstruct[];
}

/**
 * Lit un sous-arbre `spec.inputs` / `spec.outputs`.
 *
 * Deux issues seulement, jamais une troisième : soit le schéma tient dans le constructeur et
 * devient une liste de champs, soit il n'y tient pas et il est gardé **entier** dans `raw`, avec la
 * liste précise de ce qui l'a disqualifié.
 *
 * L'opacité est décidée pour le schéma entier, pas propriété par propriété. Un schéma JSON est une
 * seule unité de sens : éditer trois propriétés au formulaire pendant qu'un `oneOf` invisible en
 * contraint une quatrième, c'est fabriquer des manifestes contradictoires sans que personne ne
 * puisse voir pourquoi.
 */
export function importSchemaSection(raw: JsonValue | undefined, basePath: string): SchemaImport {
  if (raw === undefined) {
    return { section: { present: false, editable: true, fields: [], raw: null }, issues: [] };
  }

  const issues: UnsupportedConstruct[] = [];
  const opaque = (): SchemaImport => ({
    section: { present: true, editable: false, fields: [], raw },
    issues,
  });

  if (!isJsonObject(raw)) {
    issues.push({ path: basePath, reason: 'schemaShape' });
    return opaque();
  }

  for (const key of Object.keys(raw)) {
    if (!SCHEMA_ROOT_KEYWORDS.has(key)) issues.push({ path: `${basePath}.${key}`, reason: 'schemaRootKeyword' });
  }

  // `type` absent vaut `object` : c'est ce que le reste de la plateforme suppose déjà d'un schéma
  // d'entrées. Toute autre valeur décrit autre chose qu'un jeu de champs.
  if (raw['type'] !== undefined && raw['type'] !== 'object') {
    issues.push({ path: `${basePath}.type`, reason: 'schemaShape' });
  }

  const required = readRequired(raw['required'], basePath, issues);
  const properties = raw['properties'];
  if (properties !== undefined && !isJsonObject(properties)) {
    issues.push({ path: `${basePath}.properties`, reason: 'schemaShape' });
  }

  if (issues.length > 0) return opaque();

  const fields: ManifestField[] = [];
  for (const [name, property] of Object.entries((properties as JsonObject | undefined) ?? {})) {
    const field = readProperty(name, property, `${basePath}.properties.${name}`, required, issues);
    if (field) fields.push(field);
  }

  if (issues.length > 0) return opaque();

  // Un `required` qui nomme une propriété absente ne se réémettrait pas : le champ n'existe pas.
  for (const name of required) {
    if (!fields.some((f) => f.name === name)) {
      issues.push({ path: `${basePath}.required.${name}`, reason: 'schemaShape' });
    }
  }

  if (issues.length > 0) return opaque();

  return { section: { present: true, editable: true, fields, raw }, issues: [] };
}

function readRequired(value: JsonValue | undefined, basePath: string, issues: UnsupportedConstruct[]): string[] {
  if (value === undefined) return [];
  if (!Array.isArray(value) || value.some((entry) => typeof entry !== 'string')) {
    issues.push({ path: `${basePath}.required`, reason: 'schemaShape' });
    return [];
  }
  return value as string[];
}

function readProperty(
  name: string,
  property: JsonValue,
  path: string,
  required: string[],
  issues: UnsupportedConstruct[],
): ManifestField | null {
  if (!isJsonObject(property)) {
    issues.push({ path, reason: 'schemaShape' });
    return null;
  }

  let rejected = false;
  for (const key of Object.keys(property)) {
    if (!PROPERTY_KEYWORDS.has(key)) {
      issues.push({ path: `${path}.${key}`, reason: 'schemaPropertyKeyword' });
      rejected = true;
    }
  }

  const type = property['type'];
  if (typeof type !== 'string' || !MANIFEST_FIELD_TYPES.includes(type as ManifestFieldType)) {
    issues.push({ path: `${path}.type`, reason: 'schemaPropertyType' });
    rejected = true;
  }

  for (const key of ['title', 'description'] as const) {
    const value = property[key];
    if (value !== undefined && typeof value !== 'string') {
      issues.push({ path: `${path}.${key}`, reason: 'schemaValue' });
      rejected = true;
    }
  }

  const defaultValue = property['default'];
  if (defaultValue !== undefined && !(isScalar(defaultValue) && isStableScalar(defaultValue))) {
    issues.push({ path: `${path}.default`, reason: 'schemaValue' });
    rejected = true;
  }

  const enumValues = property['enum'];
  if (enumValues !== undefined) {
    const usable =
      Array.isArray(enumValues) &&
      enumValues.length > 0 &&
      enumValues.every((entry) => isScalar(entry) && isStableScalar(entry));
    if (!usable) {
      issues.push({ path: `${path}.enum`, reason: 'schemaValue' });
      rejected = true;
    }
  }

  if (rejected) return null;

  return {
    name,
    type: type as ManifestFieldType,
    title: (property['title'] as string | undefined) ?? '',
    description: (property['description'] as string | undefined) ?? '',
    required: required.includes(name),
    defaultValue: defaultValue === undefined ? null : defaultValue,
    enumValues: enumValues === undefined ? null : (enumValues as JsonValue[]),
  };
}

/**
 * L'inverse : le sous-arbre JSON à réémettre pour cette section, ou `null` s'il n'y a rien à écrire.
 * Une section non éditable ressort telle qu'elle est entrée.
 */
export function schemaSectionToJson(section: ManifestSchemaSection): JsonValue | null {
  if (!section.present) return null;
  if (!section.editable) return section.raw;

  const properties: JsonObject = {};
  const required: string[] = [];
  for (const field of section.fields) {
    if (!field.name) continue;
    const property: JsonObject = { type: field.type };
    if (field.title) property['title'] = field.title;
    if (field.description) property['description'] = field.description;
    if (field.defaultValue !== null) property['default'] = field.defaultValue;
    if (field.enumValues && field.enumValues.length > 0) property['enum'] = field.enumValues;
    properties[field.name] = property;
    if (field.required) required.push(field.name);
  }

  const schema: JsonObject = { type: 'object', properties };
  if (required.length > 0) schema['required'] = required;
  return schema;
}
