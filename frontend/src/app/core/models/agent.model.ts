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

export interface CreateAgentRequest {
  projectId: string;
  name: string;
  slug: string;
  agentType: AgentType;
  imageRef?: string;
  manifestYaml: string;
  inputsSchema: JsonSchema;
  outputsSchema?: JsonSchema;
}
