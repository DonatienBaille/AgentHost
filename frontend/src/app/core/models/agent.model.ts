import { JsonSchema } from './json-schema.model';

export type AgentType = 'oci' | 'copilot' | 'claude_code' | 'openai' | 'custom';

export interface Agent {
  id: string;
  orgId: string;
  projectId: string;
  name: string;
  slug: string;
  agentType: AgentType;
  imageRef: string | null;
  manifestYaml: string;
  inputsSchema: JsonSchema;
  outputsSchema: JsonSchema | null;
  currentVersionId: string;
  isPublished: boolean;
  createdAt: string;
  updatedAt: string;
}

/**
 * Matches backend Contracts/AgentContracts.cs — the server derives name/slug/schemas
 * from the manifest YAML rather than accepting them as separate fields.
 */
export interface CreateAgentRequest {
  projectId: string;
  name: string;
  slug: string;
  manifestYaml: string;
  publish?: boolean;
}

/** One published/draft version of an agent's manifest (backend Domain/AgentVersion.cs). */
export interface AgentVersion {
  id: string;
  agentId: string;
  versionNumber: number;
  manifestYaml: string;
  imageRef: string | null;
  inputsSchema: string;
  outputsSchema: string | null;
  digestSha256: string;
  createdAt: string;
}

export interface PublishAgentVersionRequest {
  manifestYaml: string;
}
