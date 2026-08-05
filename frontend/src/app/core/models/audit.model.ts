export interface AuditLogEntry {
  id: string;
  orgId: string;
  action: string;
  actorUserId: string | null;
  resourceType: string | null;
  resourceId: string | null;
  changes: unknown;
  details: unknown;
  createdAt: string;
}
