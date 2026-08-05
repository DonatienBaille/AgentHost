import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateOrganizationRequest, Organization } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/organizations`;

@Injectable({ providedIn: 'root' })
export class OrganizationService {
  private readonly http = inject(HttpClient);

  readonly organizations = signal<Organization[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  async listOrganizations(): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(this.http.get<Organization[]>(BASE_URL));
      this.organizations.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('Failed to load organizations');
    } finally {
      this.isLoading.set(false);
    }
  }

  async createOrganization(req: CreateOrganizationRequest): Promise<Organization> {
    const org = await firstValueFrom(this.http.post<Organization>(BASE_URL, req));
    this.organizations.set([...this.organizations(), org]);
    return org;
  }
}
