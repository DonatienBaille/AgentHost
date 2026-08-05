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
