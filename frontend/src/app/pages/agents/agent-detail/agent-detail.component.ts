import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { ActivatedRoute } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { ManifestEditorComponent } from '../../../components/manifest-editor/manifest-editor.component';
import { AgentService } from '../../../services/agent.service';
import { AuthService } from '../../../services/auth.service';
import { Agent } from '../../../core/models';

@Component({
  selector: 'app-agent-detail',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, TranslatePipe, ManifestEditorComponent],
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
    let loaded = false;
    try {
      const agent = await this.agentService.fetchAgent(id);
      this.agent.set(agent);
      this.publishForm.patchValue({ manifestYaml: agent.manifestYaml });
      this.loadError.set(null);
      loaded = true;
    } catch {
      // L'agent précédent doit disparaître : sur une navigation d'un agent existant vers un
      // identifiant inconnu, le garder afficherait le mauvais agent sous un message d'erreur.
      this.agent.set(null);
      this.loadError.set('agentDetail.loadError');
    } finally {
      this.isLoading.set(false);
    }

    // Inutile de demander les versions d'un agent qui n'existe pas : c'est un second appel voué à
    // échouer, dont la seule conséquence visible serait une deuxième bulle d'erreur.
    if (!loaded) {
      this.agentService.versions.set([]);
      return;
    }

    try {
      await this.agentService.listVersions(id);
    } catch {
      // surfaced via agentService.error already
    }
  }

  /**
   * Le manifeste ne sort plus d'un `<textarea>` mais de l'éditeur à deux modes. Il continue de
   * passer par le contrôle réactif : c'est lui qui porte la validation « non vide » et c'est lui
   * que `publish()` lit.
   */
  onManifestYaml(manifestYaml: string): void {
    this.publishForm.controls.manifestYaml.setValue(manifestYaml);
  }

  async publish(): Promise<void> {
    // Revérifié hors du gabarit : le `@if` masque le bouton, il n'empêche pas d'appeler la
    // méthode. Le serveur reste l'autorité — c'est de la défense en profondeur.
    if (!this.canPublish()) return;

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
