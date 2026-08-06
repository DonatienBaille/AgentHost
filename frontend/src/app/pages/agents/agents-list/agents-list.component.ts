import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { AgentService } from '../../../services/agent.service';
import { AuthService } from '../../../services/auth.service';

@Component({
  selector: 'app-agents-list',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe],
  templateUrl: './agents-list.component.html',
  styleUrls: ['./agents-list.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AgentsListComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly agentService = inject(AgentService);
  private readonly authService = inject(AuthService);
  private readonly route = inject(ActivatedRoute);

  readonly agents = this.agentService.agents;
  readonly isLoading = this.agentService.isLoading;
  readonly error = this.agentService.error;

  readonly canCreate = this.authService.isDeveloperOrAbove;
  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);

  /** Set when this page is reached via projects/:id/agents; null for the global agents list. */
  readonly projectId = signal<string | null>(null);

  readonly pageTitle = computed(() =>
    this.projectId() ? 'agentsList.titleForProject' : 'agentsList.title',
  );

  readonly form = this.fb.group({
    name: ['', [Validators.required]],
    slug: ['', [Validators.required, Validators.pattern(/^[a-z0-9-]+$/)]],
    manifestYaml: ['', [Validators.required]],
    publish: [true],
  });

  ngOnInit(): void {
    this.route.params.subscribe((params) => {
      const projectId = params['id'] ?? null;
      this.projectId.set(projectId);
      this.agentService.listAgents(projectId ?? undefined);
    });
  }

  toggleForm(): void {
    this.showForm.set(!this.showForm());
  }

  async submit(): Promise<void> {
    // Revérifié hors du gabarit : le `@if` masque le bouton, il n'empêche pas d'appeler la
    // méthode. Le serveur reste l'autorité — c'est de la défense en profondeur.
    if (!this.canCreate()) return;

    const projectId = this.projectId();
    if (!projectId || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.submitError.set(null);
    try {
      const { name, slug, manifestYaml, publish } = this.form.getRawValue();
      await this.agentService.createAgent({
        projectId,
        name: name!,
        slug: slug!,
        manifestYaml: manifestYaml!,
        publish: publish ?? true,
      });
      this.form.reset({ publish: true });
      this.showForm.set(false);
    } catch (err) {
      this.submitError.set('agentsList.createError');
    } finally {
      this.isSubmitting.set(false);
    }
  }
}
