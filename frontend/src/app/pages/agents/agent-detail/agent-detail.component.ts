import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AgentService } from '../../../services/agent.service';
import { AuthService } from '../../../services/auth.service';
import { Agent } from '../../../core/models';

@Component({
  selector: 'app-agent-detail',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, TranslatePipe],
  templateUrl: './agent-detail.component.html',
  styleUrls: ['./agent-detail.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgentDetailComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly agentService = inject(AgentService);
  private readonly authService = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  readonly agent = signal<Agent | null>(null);
  readonly isLoading = signal(false);
  readonly loadError = signal<string | null>(null);

  readonly versions = this.agentService.versions;
  readonly isLoadingVersions = this.agentService.isLoadingVersions;

  readonly canPublish = this.authService.isDeveloperOrAbove;
  readonly isPublishing = signal(false);
  readonly publishError = signal<string | null>(null);

  readonly publishForm = this.fb.group({
    manifestYaml: ['', [Validators.required]],
  });

  private agentId: string | null = null;

  ngOnInit(): void {
    this.route.params.subscribe((params) => {
      const id = params['id'];
      if (id) {
        this.agentId = id;
        this.load(id);
      }
    });
  }

  private async load(id: string): Promise<void> {
    this.isLoading.set(true);
    try {
      const agent = await this.agentService.fetchAgent(id);
      this.agent.set(agent);
      if (agent) {
        this.publishForm.patchValue({ manifestYaml: agent.manifestYaml });
      }
      this.loadError.set(null);
    } catch {
      this.loadError.set('agentDetail.loadError');
    } finally {
      this.isLoading.set(false);
    }

    try {
      await this.agentService.listVersions(id);
    } catch {
      // surfaced via agentService.error already
    }
  }

  async publish(): Promise<void> {
    const id = this.agentId;
    if (!id || this.publishForm.invalid) {
      this.publishForm.markAllAsTouched();
      return;
    }

    this.isPublishing.set(true);
    this.publishError.set(null);
    try {
      const { manifestYaml } = this.publishForm.getRawValue();
      await this.agentService.publishVersion(id, manifestYaml!);
      await this.load(id);
    } catch {
      this.publishError.set('agentDetail.publishError');
    } finally {
      this.isPublishing.set(false);
    }
  }
}
