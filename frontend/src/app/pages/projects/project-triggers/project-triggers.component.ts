import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { TriggerService } from '../../../services/trigger.service';
import { AgentService } from '../../../services/agent.service';
import { AuthService } from '../../../services/auth.service';
import { CreateTriggerRequest, Trigger, TriggerType } from '../../../core/models';

/**
 * Les déclencheurs d'un projet : ce qui fait partir un agent sans que personne ne clique
 * (feuille de route, lot 4).
 *
 * <b>Le secret n'est montré qu'une fois, et l'écran le dit.</b> Le serveur ne le rend qu'à la
 * création — il n'est stocké que chiffré. Un bandeau persistant le porte donc jusqu'à ce que
 * l'utilisateur le ferme explicitement : le faire disparaître à la première navigation le perdrait
 * définitivement, et il n'y a pas de « mot de passe oublié » pour un webhook.
 *
 * <b>Deux natures, un seul formulaire.</b> Webhook et cron répondent à la même question — à quelle
 * occasion ce run part-il — et ne diffèrent que par trois champs. Deux formulaires côte à côte
 * feraient choisir avant de savoir ce qu'on choisit.
 */
@Component({
  selector: 'app-project-triggers',
  standalone: true,
  imports: [DatePipe, ReactiveFormsModule, TranslatePipe],
  templateUrl: './project-triggers.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectTriggersComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly route = inject(ActivatedRoute);
  private readonly triggerService = inject(TriggerService);
  private readonly agentService = inject(AgentService);
  private readonly authService = inject(AuthService);

  readonly triggers = this.triggerService.triggers;
  readonly isLoading = this.triggerService.isLoading;
  readonly error = this.triggerService.error;
  readonly agents = this.agentService.agents;

  /**
   * Le serveur exige `maintainer` pour créer, modifier ou supprimer un déclencheur : en poser un,
   * c'est donner à un tiers le droit de dépenser le budget du projet. La lecture, elle, n'engage
   * rien et reste ouverte à tous les rôles — d'où une page visible partout et des actions gardées.
   */
  readonly canManage = this.authService.isMaintainerOrAbove;

  readonly projectId = signal('');
  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);
  readonly busyTriggerId = signal<string | null>(null);

  /** Le secret fraîchement créé, affiché jusqu'à ce que l'utilisateur le ferme. */
  readonly revealedSecret = signal<{ name: string; secret: string; path: string } | null>(null);

  readonly form = this.fb.group({
    name: ['', [Validators.required]],
    type: ['webhook' as TriggerType, [Validators.required]],
    agentId: ['', [Validators.required]],
    provider: ['github'],
    events: [''],
    branches: [''],
    cronExpression: [''],
    timeZone: ['UTC'],
  });

  /** Seuls les agents publiés sont lançables ; en proposer d'autres promettrait un run impossible. */
  readonly publishedAgents = computed(() => this.agents().filter((a) => a.isPublished));

  readonly selectedType = signal<TriggerType>('webhook');

  ngOnInit(): void {
    const id = this.route.snapshot.paramMap.get('id') ?? '';
    this.projectId.set(id);
    if (!id) return;

    void this.triggerService.list(id);
    void this.agentService.listAgents(id);
  }

  toggleForm(): void {
    this.showForm.update((open) => !open);
    this.submitError.set(null);
  }

  onTypeChange(type: TriggerType): void {
    this.selectedType.set(type);
    this.form.patchValue({ type });
  }

  async submit(): Promise<void> {
    // Le serveur reste l'autorité ; cette garde évite un formulaire rempli pour rien.
    if (!this.canManage() || this.form.invalid || this.isSubmitting()) return;

    this.isSubmitting.set(true);
    this.submitError.set(null);

    const value = this.form.getRawValue();
    const request: CreateTriggerRequest = {
      agentId: value.agentId!,
      name: value.name!,
      type: value.type!,
    };

    if (value.type === 'webhook') {
      request.provider = (value.provider || 'generic') as CreateTriggerRequest['provider'];
      request.events = ProjectTriggersComponent.splitList(value.events);
      request.branches = ProjectTriggersComponent.splitList(value.branches);
    } else {
      request.cronExpression = value.cronExpression ?? '';
      request.timeZone = value.timeZone || 'UTC';
    }

    try {
      const created = await this.triggerService.create(this.projectId(), request);
      if (created.secret) {
        this.revealedSecret.set({
          name: created.trigger.name,
          secret: created.secret,
          path: created.trigger.webhookPath ?? '',
        });
      }
      this.form.reset({ type: this.selectedType(), provider: 'github', timeZone: 'UTC' });
      this.showForm.set(false);
    } catch (err) {
      // Le message du serveur porte la raison exacte — expression cron illisible, fuseau inconnu.
      // Le remplacer par « une erreur est survenue » enlèverait le seul moyen de corriger.
      this.submitError.set(ProjectTriggersComponent.reasonOf(err));
    } finally {
      this.isSubmitting.set(false);
    }
  }

  async toggleActive(trigger: Trigger): Promise<void> {
    if (!this.canManage()) return;
    this.busyTriggerId.set(trigger.id);
    try {
      await this.triggerService.update(trigger.id, { isActive: !trigger.isActive });
    } catch {
      this.triggerService.error.set('errors.updateTrigger');
    } finally {
      this.busyTriggerId.set(null);
    }
  }

  async remove(trigger: Trigger): Promise<void> {
    if (!this.canManage()) return;
    this.busyTriggerId.set(trigger.id);
    try {
      await this.triggerService.remove(trigger.id);
    } catch {
      this.triggerService.error.set('errors.deleteTrigger');
    } finally {
      this.busyTriggerId.set(null);
    }
  }

  dismissSecret(): void {
    this.revealedSecret.set(null);
  }

  /** L'URL complète à coller chez l'émetteur : le serveur ne connaît pas son hôte public. */
  absoluteHookUrl(path: string): string {
    return `${window.location.origin}${path}`;
  }

  agentName(agentId: string): string {
    return this.agents().find((a) => a.id === agentId)?.name ?? agentId;
  }

  /** « push, pull_request » → ['push', 'pull_request']. Un champ vide ne filtre rien. */
  private static splitList(value: string | null | undefined): string[] {
    return (value ?? '')
      .split(',')
      .map((v) => v.trim())
      .filter((v) => v.length > 0);
  }

  private static reasonOf(err: unknown): string {
    const message = (err as { error?: { error?: string } })?.error?.error;
    return message && message.length > 0 ? message : 'errors.createTrigger';
  }
}
