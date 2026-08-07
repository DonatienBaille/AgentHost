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

/**
 * Une entrée telle que la consultation la sert : la ligne, plus l'identité de son acteur.
 *
 * `orgId` n'y figure pas, et c'est voulu : le périmètre vient du jeton de l'appelant, le serveur
 * n'a donc rien à répéter et l'IHM n'a rien à en faire.
 */
export interface AuditEntryView {
  id: string;
  action: string;
  actorUserId: string | null;
  /** Résolus par le serveur : la table ne stocke qu'un ULID, que personne ne reconnaît. */
  actorEmail: string | null;
  actorDisplayName: string | null;
  resourceType: string | null;
  resourceId: string | null;
  changes: unknown;
  details: unknown;
  createdAt: string;
}

/** Une page de journal, avec le total du jeu filtré — sans lui, la pagination navigue à l'aveugle. */
export interface AuditPage {
  items: AuditEntryView[];
  total: number;
  skip: number;
  take: number;
}

export interface AuditFacetValue {
  value: string;
  count: number;
}

export interface AuditActorFacet {
  userId: string;
  email: string | null;
  displayName: string | null;
  count: number;
}

/**
 * Ce que le journal contient réellement, servi pour construire les filtres.
 *
 * C'est ce qui permet d'offrir des listes de choix plutôt qu'un champ libre : les actions sont un
 * vocabulaire fermé côté serveur, et une faute de frappe dans un champ libre rendrait un journal
 * vide qu'on lirait comme « il ne s'est rien passé ».
 */
export interface AuditFacets {
  actions: AuditFacetValue[];
  resourceTypes: AuditFacetValue[];
  actors: AuditActorFacet[];
  earliestEntry: string | null;
  totalEntries: number;
}

/** Les filtres appliqués à une consultation. Une propriété absente = aucune contrainte. */
export interface AuditFilters {
  action?: string;
  actorUserId?: string;
  resourceType?: string;
  resourceId?: string;
  /** Bornes de période au format `YYYY-MM-DD`, telles que les rend un `<input type="date">`. */
  from?: string;
  to?: string;
}
