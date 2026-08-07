/**
 * Le document de manifeste tel qu'il voyage entre le serveur et l'éditeur : du JSON, rien de plus.
 *
 * Le backend projette le YAML dans cette forme (`Services/YamlDocumentProjection`) en résolvant les
 * scalaires avec le schéma « core » de YAML 1.2 : `default: 5` reste le nombre 5, `default: "5"`
 * reste la chaîne « 5 ». Cette distinction est ce qui permet à l'éditeur de réémettre un schéma
 * sans le déformer.
 */
export type JsonValue = string | number | boolean | null | JsonValue[] | JsonObject;

export interface JsonObject {
  [key: string]: JsonValue;
}

export function isJsonObject(value: JsonValue | undefined): value is JsonObject {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

export function isScalar(value: JsonValue | undefined): value is string | number | boolean | null {
  return value === null || ['string', 'number', 'boolean'].includes(typeof value);
}

/**
 * Relit le texte d'un champ de formulaire comme YAML le relirait : `true` est un booléen, `12` un
 * nombre, `null` le vide, tout le reste une chaîne.
 *
 * C'est la règle qui rend l'aller-retour stable. Le formulaire n'édite que du texte ; si la relecture
 * suivait le type déclaré du champ plutôt que la forme du texte, `default: 3` sur un champ passé de
 * `integer` à `string` deviendrait `"3"` sans que personne ne l'ait demandé.
 */
export function parseScalarText(text: string): JsonValue {
  if (text === 'true' || text === 'True' || text === 'TRUE') return true;
  if (text === 'false' || text === 'False' || text === 'FALSE') return false;
  if (text === 'null' || text === 'Null' || text === 'NULL' || text === '~') return null;
  if (/^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$/.test(text)) {
    const parsed = Number(text);
    if (Number.isFinite(parsed)) return parsed;
  }
  return text;
}

/** L'inverse : ce qu'on affiche dans le champ texte pour un scalaire du document. */
export function formatScalarText(value: string | number | boolean | null): string {
  if (value === null) return 'null';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'number') return String(value);
  return value;
}

/**
 * Ce scalaire survit-il à l'aller-retour « affiché en texte puis relu » ?
 *
 * Non pour la chaîne `"5"` (relue en nombre) et non pour la chaîne vide (indiscernable d'un champ
 * qu'on a laissé vide, donc d'une valeur absente). Ce sont exactement les cas où le constructeur de
 * champs mentirait sur ce qu'il édite : on préfère alors ne pas y toucher du tout.
 */
export function isStableScalar(value: JsonValue): boolean {
  if (value === null || typeof value === 'boolean') return true;
  if (typeof value === 'number') return Number.isFinite(value);
  if (typeof value !== 'string') return false;
  if (value === '') return false;
  return parseScalarText(value) === value;
}
