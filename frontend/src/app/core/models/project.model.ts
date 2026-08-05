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

export interface CreateProjectRequest {
  orgId: string;
  name: string;
  slug: string;
  description?: string;
  budgetMonthlyUsd?: number;
}
