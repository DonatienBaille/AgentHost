import { Injectable, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { Artifact } from '../core/models';

@Injectable({ providedIn: 'root' })
export class ArtifactService {
  private readonly http = inject(HttpClient);

  readonly artifacts = signal<Artifact[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  async listArtifacts(runId: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(
        this.http.get<Artifact[]>(`${environment.apiUrl}/api/runs/${runId}/artifacts`),
      );
      this.artifacts.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('Failed to load artifacts');
    } finally {
      this.isLoading.set(false);
    }
  }

  /**
   * Downloads the artifact through HttpClient (so the auth interceptor attaches the
   * Bearer token) and triggers a client-side save via a temporary object URL — a plain
   * <a href> would hit the authenticated endpoint without credentials.
   */
  async download(artifact: Artifact): Promise<void> {
    const blob = await firstValueFrom(
      this.http.get(`${environment.apiUrl}/api/artifacts/${artifact.id}/download`, {
        responseType: 'blob',
      }),
    );
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = artifact.name;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
  }
}
