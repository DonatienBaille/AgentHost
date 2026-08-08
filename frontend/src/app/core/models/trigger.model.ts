/** Nature d'un déclencheur : ce qui le fait partir. */
export type TriggerType = 'webhook' | 'cron';

/**
 * L'émetteur d'un webhook entrant, qui détermine comment sa signature se vérifie.
 *
 * Ce n'est pas une préférence : GitHub calcule un HMAC sur le corps brut, GitLab envoie le secret
 * en clair dans un en-tête. Le choix conditionne ce que le serveur attend.
 */
export type TriggerProvider = 'github' | 'gitlab' | 'generic';

/**
 * Un déclencheur tel que l'API le sert.
 *
 * <b>Le secret n'y figure pas.</b> Il n'est rendu qu'une fois, à la création : un secret que l'API
 * redonne à volonté n'est plus protégé par le chiffrement au repos.
 */
export interface Trigger {
  id: string;
  projectId: string;
  agentId: string;
  type: TriggerType;
  name: string;
  isActive: boolean;
  inputs: unknown;

  provider: TriggerProvider | null;
  events: string[];
  branches: string[];
  /** Chemin à configurer chez l'émetteur, relatif : l'hôte public n'est pas connu du serveur. */
  webhookPath: string | null;

  cronExpression: string | null;
  timeZone: string;
  nextRunAt: string | null;
  lastRunAt: string | null;
  lastRunId: string | null;

  createdAt: string;
}

export interface CreateTriggerRequest {
  agentId: string;
  name: string;
  type: TriggerType;
  inputs?: unknown;
  provider?: TriggerProvider;
  events?: string[];
  branches?: string[];
  cronExpression?: string;
  timeZone?: string;
}

/** La réponse de création : le déclencheur, plus le secret, qui ne repassera plus. */
export interface CreateTriggerResponse {
  trigger: Trigger;
  secret: string | null;
}

export interface UpdateTriggerRequest {
  name?: string;
  isActive?: boolean;
  events?: string[];
  branches?: string[];
  cronExpression?: string;
  timeZone?: string;
}
