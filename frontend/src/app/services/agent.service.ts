import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import {
  Agent,
  AgentVersion,
  CreateAgentRequest,
  ManifestValidation,
  PublishAgentVersionRequest,
} from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/agents`;

@Injectable({ providedIn: 'root' })
export class AgentService {
  private readonly http = inject(HttpClient);

  readonly agents = signal<Agent[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  readonly versions = signal<AgentVersion[]>([]);
  readonly isLoadingVersions = signal(false);

  readonly agentCount = computed(() => this.agents().length);

  /** Lists agents, optionally scoped to a project (backend: GET /api/agents?projectId=). */
  async listAgents(projectId?: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const url = projectId ? `${BASE_URL}?projectId=${encodeURIComponent(projectId)}` : BASE_URL;
      const data = await firstValueFrom(this.http.get<Agent[]>(url));
      this.agents.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('errors.loadAgents');
    } finally {
      this.isLoading.set(false);
    }
  }

  /**
   * Charge un agent, ou **lève** si le serveur refuse.
   *
   * Cette méthode renvoyait `null` en cas d'échec en s'étant contentée de poser `error`. Le
   * `catch` de l'appelant ne s'exécutait donc jamais : sur un identifiant inconnu, la page de
   * détail affichait un corps entièrement vide — ni agent, ni erreur, ni « introuvable ». Un
   * `Agent | null` où `null` veut dire « ça a échoué mais je l'ai noté ailleurs » est précisément
   * le contrat qui produit ce genre de page morte.
   */
  async fetchAgent(id: string): Promise<Agent> {
    this.isLoading.set(true);
    try {
      const agent = await firstValueFrom(this.http.get<Agent>(`${BASE_URL}/${id}`));
      const agents = this.agents();
      const index = agents.findIndex((a) => a.id === id);
      if (index >= 0) {
        const copy = [...agents];
        copy[index] = agent;
        this.agents.set(copy);
      } else {
        this.agents.set([...agents, agent]);
      }
      this.error.set(null);
      return agent;
    } catch (err) {
      this.error.set('errors.loadAgent');
      throw err;
    } finally {
      this.isLoading.set(false);
    }
  }

  async createAgent(req: CreateAgentRequest): Promise<Agent> {
    const agent = await firstValueFrom(this.http.post<Agent>(BASE_URL, req));
    this.agents.set([...this.agents(), agent]);
    return agent;
  }

  /**
   * Dry-runs the manifest through the server's own parser (backend:
   * POST /api/agents/validate-manifest). Creates nothing.
   *
   * Deliberately outside the `error` signal: the editor shows the failure inline, on the offending
   * line, and a manifest that does not parse yet is not a page-level error.
   */
  async validateManifest(manifestYaml: string): Promise<ManifestValidation> {
    return firstValueFrom(
      this.http.post<ManifestValidation>(`${BASE_URL}/validate-manifest`, { manifestYaml }),
    );
  }

  /** Version history for an agent (backend: GET /api/agents/{id}/versions). */
  async listVersions(agentId: string): Promise<AgentVersion[]> {
    this.isLoadingVersions.set(true);
    try {
      const versions = await firstValueFrom(
        this.http.get<AgentVersion[]>(`${BASE_URL}/${agentId}/versions`),
      );
      this.versions.set(versions ?? []);
      return versions ?? [];
    } catch (err) {
      this.error.set('errors.loadAgentVersions');
      throw err;
    } finally {
      this.isLoadingVersions.set(false);
    }
  }

  /** Publishes a new manifest version (backend: POST /api/agents/{id}/versions). */
  async publishVersion(agentId: string, manifestYaml: string): Promise<AgentVersion> {
    const req: PublishAgentVersionRequest = { manifestYaml };
    const version = await firstValueFrom(
      this.http.post<AgentVersion>(`${BASE_URL}/${agentId}/versions`, req),
    );
    this.versions.set([version, ...this.versions()]);
    return version;
  }
}
