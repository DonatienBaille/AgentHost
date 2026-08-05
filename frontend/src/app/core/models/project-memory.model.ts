export interface ProjectContext {
  name: string;
  description: string;
  technologies: string[];
  recentDecisions: string[];
}

export interface RunHistoryItem {
  runId: string;
  agentName: string;
  timestamp: string;
  outcome: string;
  summary: string;
}

export interface MemoryPattern {
  name: string;
  frequency: number;
  lastSeen: string;
}

export interface Learning {
  id: string;
  title: string;
  description: string;
  createdAt: string;
  appliedByAgents: string[];
}

export interface MemoryNote {
  id: string;
  agentName: string;
  text: string;
  timestamp: string;
}

export interface ArchivePeriod {
  period: string;
  summary: string;
  createdAt: string;
}

export interface ProjectMemory {
  projectId: string;
  context: ProjectContext;
  runHistory: RunHistoryItem[];
  patterns: MemoryPattern[];
  learnings: Learning[];
  notes: MemoryNote[];
  archived: ArchivePeriod[];
  createdAt: string;
  updatedAt: string;
}

/** Payload sent to AgentMemoryHub.UpdateMemory / received via memoryUpdated. */
export interface MemoryUpdate {
  note?: Partial<MemoryNote>;
  learning?: Partial<Learning>;
  [key: string]: unknown;
}
