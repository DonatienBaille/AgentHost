import { JsonObject, JsonValue, isJsonObject, parseScalarText } from './json-value';

/**
 * Émetteur YAML canonique, en style bloc, indentation de deux espaces.
 *
 * Volontairement minuscule et sans dépendance : il n'a qu'un arbre JSON à écrire, jamais à lire.
 * La lecture, elle, reste au serveur — `POST /api/agents/validate-manifest` fait tourner le vrai
 * `IAgentManifestParser`. Un second parseur YAML dans le navigateur voudrait dire deux vérités
 * possibles sur un même fichier ; il n'y en a qu'une.
 *
 * « Canonique » ici veut dire : même modèle, même texte, à l'octet près. C'est ce qui rend l'aperçu
 * en direct stable et le test d'aller-retour possible.
 */
export function emitYaml(value: JsonValue): string {
  const lines: string[] = [];
  writeValue(value, 0, lines);
  return lines.length === 0 ? '' : lines.join('\n') + '\n';
}

function indent(level: number): string {
  return '  '.repeat(level);
}

function writeValue(value: JsonValue, level: number, lines: string[]): void {
  if (Array.isArray(value)) {
    if (value.length === 0) lines.push(indent(level) + '[]');
    else writeSequence(value, level, lines);
  } else if (isJsonObject(value)) {
    if (Object.keys(value).length === 0) lines.push(indent(level) + '{}');
    else writeMapping(value, level, lines);
  } else {
    lines.push(indent(level) + emitScalar(value as string | number | boolean | null));
  }
}

function writeMapping(map: JsonObject, level: number, lines: string[]): void {
  for (const [key, child] of Object.entries(map)) {
    const prefix = `${indent(level)}${emitKey(key)}:`;
    if (Array.isArray(child)) {
      if (child.length === 0) lines.push(`${prefix} []`);
      else {
        lines.push(prefix);
        // Les tirets s'alignent sur la clé : c'est la forme la plus courante et YAML l'accepte.
        writeSequence(child, level, lines);
      }
    } else if (isJsonObject(child)) {
      if (Object.keys(child).length === 0) lines.push(`${prefix} {}`);
      else {
        lines.push(prefix);
        writeMapping(child, level + 1, lines);
      }
    } else {
      lines.push(`${prefix} ${emitScalar(child as string | number | boolean | null)}`);
    }
  }
}

function writeSequence(items: JsonValue[], level: number, lines: string[]): void {
  for (const item of items) {
    const isBlock =
      (Array.isArray(item) && item.length > 0) ||
      (isJsonObject(item) && Object.keys(item).length > 0);

    if (!isBlock) {
      lines.push(`${indent(level)}- ${emitScalarOrEmptyContainer(item)}`);
      continue;
    }

    // Le contenu est écrit un niveau plus bas, puis sa première ligne remonte derrière le tiret ;
    // les suivantes sont déjà alignées dessous.
    const nested: string[] = [];
    writeValue(item, level + 1, nested);
    lines.push(`${indent(level)}- ${nested[0].trimStart()}`);
    for (let i = 1; i < nested.length; i++) lines.push(nested[i]);
  }
}

function emitScalarOrEmptyContainer(value: JsonValue): string {
  if (Array.isArray(value)) return '[]';
  if (isJsonObject(value)) return '{}';
  return emitScalar(value as string | number | boolean | null);
}

function emitScalar(value: string | number | boolean | null): string {
  if (value === null) return 'null';
  if (typeof value === 'boolean') return value ? 'true' : 'false';
  if (typeof value === 'number') {
    // Ni Infinity ni NaN ne sont du JSON ; s'ils arrivaient, une chaîne citée vaut mieux qu'un
    // document YAML illisible.
    return Number.isFinite(value) ? String(value) : `'${String(value)}'`;
  }
  return needsQuotes(value) ? quote(value) : value;
}

function emitKey(key: string): string {
  return needsQuotes(key) ? quote(key) : key;
}

/**
 * Cite dès qu'un doute existe. Sur-citer est sans conséquence ; sous-citer change le sens du
 * document — `on`, `12`, `- x` ou une chaîne vide se relisent en autre chose.
 */
function needsQuotes(text: string): boolean {
  if (text === '') return true;
  if (parseScalarText(text) !== text) return true;
  if (text !== text.trim()) return true;
  // Un « : » ne termine une clé que suivi d'un blanc ou d'une fin de ligne. Citer sans cette
  // nuance rendrait `image: "ghcr.io/acme/x:1.4.0"` — correct, et illisible sur la clé la plus
  // courante du manifeste.
  if (/:(\s|$)/.test(text)) return true;
  // De même, un « # » n'ouvre un commentaire qu'en début de scalaire ou après un blanc.
  if (/(^|\s)#/.test(text)) return true;
  if (/[\n\r\t"'\\{}[\],&*!|>%@`]/.test(text)) return true;
  if (/^[-?]/.test(text)) return true;
  return false;
}

/**
 * `JSON.stringify` produit exactement un scalaire YAML entre guillemets doubles : YAML 1.2 reprend
 * les échappements de JSON, y compris `\n` et `\uXXXX`.
 */
function quote(text: string): string {
  return JSON.stringify(text);
}
