export type RunEventLevel = 'debug' | 'info' | 'warn' | 'error';

export interface RunEvent {
  runId: string;
  seq: number;
  timestamp: string;
  eventType: string;
  level: RunEventLevel;
  message: string | null;
  payload: Record<string, unknown> | null;
}
