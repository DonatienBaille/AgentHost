import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { Agent, CreateAgentRequest } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/agents`;

@Injectable({ providedIn: 'root' })
export class AgentService {
  private readonly http = inject(HttpClient);

  readonly agents = signal<Agent[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  readonly agentCount = computed(() => this.agents().length);

  async listAgents(): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(this.http.get<Agent[]>(BASE_URL));
      this.agents.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('Failed to load agents');
    } finally {
      this.isLoading.set(false);
    }
  }

  async fetchAgent(id: string): Promise<Agent | null> {
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
      this.error.set(`Failed to load agent ${id}`);
      return null;
    } finally {
      this.isLoading.set(false);
    }
  }

  async createAgent(req: CreateAgentRequest): Promise<Agent> {
    const agent = await firstValueFrom(this.http.post<Agent>(BASE_URL, req));
    this.agents.set([...this.agents(), agent]);
    return agent;
  }
}
