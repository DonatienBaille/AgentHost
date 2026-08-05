export interface Organization {
  id: string;
  name: string;
  slug: string;
  plan: string;
  createdAt: string;
  updatedAt: string;
}

export interface CreateOrganizationRequest {
  name: string;
  slug: string;
  plan?: string;
}
