export interface Project {
  id: string;
  orgId: string;
  name: string;
  slug: string;
  description: string | null;
  budgetMonthlyUsd: number | null;
  createdAt: string;
  updatedAt: string;
}

/** No orgId: the server takes the owning org from the caller's JWT (Contracts/ProjectContracts.cs). */
export interface CreateProjectRequest {
  name: string;
  slug: string;
  description?: string;
  budgetMonthlyUsd?: number;
}
