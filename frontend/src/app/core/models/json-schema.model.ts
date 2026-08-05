/** Minimal JSON Schema subset needed to render a dynamic form. */
export type JsonSchemaType = 'string' | 'boolean' | 'integer' | 'number' | 'object' | 'array';

export interface JsonSchemaProperty {
  type: JsonSchemaType;
  description?: string;
  default?: unknown;
  enum?: unknown[];
  title?: string;
}

export interface JsonSchema {
  type: 'object';
  required?: string[];
  properties: Record<string, JsonSchemaProperty>;
}
