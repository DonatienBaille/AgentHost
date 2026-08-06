import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from '../../environments/environment';
import { CreateProjectRequest, Project, ProjectMemory } from '../core/models';

const BASE_URL = `${environment.apiUrl}/api/projects`;

@Injectable({ providedIn: 'root' })
export class ProjectService {
  private readonly http = inject(HttpClient);

  readonly projects = signal<Project[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly currentProjectId = signal<string | null>(null);
  readonly currentMemory = signal<ProjectMemory | null>(null);

  readonly currentProject = computed(() => {
    const id = this.currentProjectId();
    return id ? (this.projects().find((p) => p.id === id) ?? null) : null;
  });

  readonly projectCount = computed(() => this.projects().length);

  async listProjects(): Promise<void> {
    this.isLoading.set(true);
    try {
      const data = await firstValueFrom(this.http.get<Project[]>(BASE_URL));
      this.projects.set(data ?? []);
      this.error.set(null);
    } catch (err) {
      this.error.set('errors.loadProjects');
    } finally {
      this.isLoading.set(false);
    }
  }

  async fetchProject(id: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const project = await firstValueFrom(this.http.get<Project>(`${BASE_URL}/${id}`));
      const projects = this.projects();
      const index = projects.findIndex((p) => p.id === id);
      if (index >= 0) {
        const copy = [...projects];
        copy[index] = project;
        this.projects.set(copy);
      } else {
        this.projects.set([...projects, project]);
      }
      this.error.set(null);
    } catch (err) {
      this.error.set('errors.loadProject');
    } finally {
      this.isLoading.set(false);
    }
  }

  async fetchMemory(id: string): Promise<ProjectMemory> {
    const memory = await firstValueFrom(
      this.http.get<ProjectMemory>(`${BASE_URL}/${id}/memory`),
    );
    this.currentMemory.set(memory);
    return memory;
  }

  selectProject(id: string): void {
    this.currentProjectId.set(id);
  }

  async createProject(req: CreateProjectRequest): Promise<Project> {
    const project = await firstValueFrom(this.http.post<Project>(BASE_URL, req));
    this.projects.set([...this.projects(), project]);
    return project;
  }
}
