import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { ProjectTriggersComponent } from './project-triggers.component';
import { TriggerService } from '../../../services/trigger.service';
import { AgentService } from '../../../services/agent.service';
import { AuthService } from '../../../services/auth.service';
import {
  Agent,
  CreateTriggerRequest,
  CreateTriggerResponse,
  Trigger,
  UpdateTriggerRequest,
  UserRole,
} from '../../../core/models';
import { agent, trigger, user } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class TriggerServiceStub {
  readonly triggers = signal<Trigger[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);

  readonly list = vi.fn(async (_projectId: string) => {});
  readonly create = vi.fn(
    async (_projectId: string, _req: CreateTriggerRequest): Promise<CreateTriggerResponse> => ({
      trigger: trigger({ id: 'created' }),
      secret: 'sh-secret-value',
    }),
  );
  readonly update = vi.fn(async (_id: string, _req: UpdateTriggerRequest) => trigger());
  readonly remove = vi.fn(async (_id: string) => {});
}

class AgentServiceStub {
  readonly agents = signal<Agent[]>([]);
  readonly listAgents = vi.fn(async (_projectId?: string) => {});
}

/**
 * La page des déclencheurs d'un projet (lot 4).
 *
 * Ce que ces tests épinglent, au-delà du rendu : le secret n'apparaît qu'après création et ne
 * disparaît que sur une action explicite — il n'y a pas de « secret oublié » pour un webhook ; le
 * message d'erreur du serveur est affiché tel quel, parce qu'il porte le champ fautif de
 * l'expression cron ; et un rôle insuffisant ne voit aucune action, le serveur exigeant
 * `maintainer` pour toutes.
 */
describe('ProjectTriggersComponent', () => {
  let fixture: ComponentFixture<ProjectTriggersComponent>;
  let triggerService: TriggerServiceStub;
  let agentService: AgentServiceStub;
  let authService: AuthService;

  function setup(role: UserRole | null = 'owner', projectId = 'p1'): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [ProjectTriggersComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: TriggerService, useClass: TriggerServiceStub },
        { provide: AgentService, useClass: AgentServiceStub },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { paramMap: convertToParamMap({ id: projectId }) } },
        },
      ],
    });

    triggerService = TestBed.inject(TriggerService) as unknown as TriggerServiceStub;
    agentService = TestBed.inject(AgentService) as unknown as AgentServiceStub;
    authService = TestBed.inject(AuthService);
    authService.currentUser.set(role ? user(role) : null);

    fixture = TestBed.createComponent(ProjectTriggersComponent);
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function all(testId: string): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`);
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('loading', () => {
    it('loads the triggers and the agents of the routed project', () => {
      setup('owner', 'proj-42');

      expect(triggerService.list).toHaveBeenCalledWith('proj-42');
      // Les agents servent la liste de choix du formulaire : sans eux, on ne peut désigner la
      // cible du déclencheur.
      expect(agentService.listAgents).toHaveBeenCalledWith('proj-42');
    });

    it('loads nothing when the route carries no project', () => {
      setup('owner', '');

      expect(triggerService.list).not.toHaveBeenCalled();
    });

    it('renders one row per trigger, and the empty state otherwise', () => {
      setup();
      expect(el('triggers-empty')).not.toBeNull();

      triggerService.triggers.set([trigger({ id: 't1' }), trigger({ id: 't2', type: 'cron' })]);
      fixture.detectChanges();

      expect(all('trigger-row').length).toBe(2);
      expect(el('triggers-empty')).toBeNull();
    });

    it('renders the service error message', () => {
      setup();
      triggerService.error.set('errors.loadTriggers');
      fixture.detectChanges();

      expect(el('triggers-error')!.textContent).toContain('errors.loadTriggers');
    });
  });

  describe('creation', () => {
    function openForm(): void {
      el('new-trigger')!.click();
      fixture.detectChanges();
    }

    it('only offers published agents', () => {
      setup();
      agentService.agents.set([
        agent({ id: 'pub', name: 'Publié', isPublished: true }),
        agent({ id: 'draft', name: 'Brouillon', isPublished: false }),
      ]);
      openForm();

      const options = el('trigger-agent')!.querySelectorAll('option');
      // Un brouillon n'est pas lançable : le proposer promettrait un run qui ne partira jamais.
      expect(options.length).toBe(2); // le placeholder + l'agent publié
      expect(options[1].textContent).toContain('Publié');
    });

    it('warns when the project has no published agent at all', () => {
      setup();
      agentService.agents.set([agent({ id: 'draft', isPublished: false })]);
      openForm();

      expect(el('no-published-agent')).not.toBeNull();
    });

    it('sends the webhook shape, with comma lists split into arrays', async () => {
      setup();
      agentService.agents.set([agent({ id: 'a1', isPublished: true })]);
      openForm();

      fixture.componentInstance.form.patchValue({
        name: 'Push',
        agentId: 'a1',
        provider: 'github',
        events: 'push, pull_request',
        branches: ' main ,release/* ',
      });
      await fixture.componentInstance.submit();

      expect(triggerService.create).toHaveBeenCalledWith('p1', {
        agentId: 'a1',
        name: 'Push',
        type: 'webhook',
        provider: 'github',
        events: ['push', 'pull_request'],
        branches: ['main', 'release/*'],
      });
    });

    it('sends the cron shape and none of the webhook fields', async () => {
      setup();
      agentService.agents.set([agent({ id: 'a1', isPublished: true })]);
      openForm();

      fixture.componentInstance.onTypeChange('cron');
      fixture.detectChanges();
      // Les champs de l'autre nature disparaissent : les laisser ferait remplir ce qui ne sera
      // pas envoyé.
      expect(el('cron-fields')).not.toBeNull();
      expect(el('webhook-fields')).toBeNull();

      fixture.componentInstance.form.patchValue({
        name: 'Rapport',
        agentId: 'a1',
        cronExpression: '0 9 * * 1-5',
        timeZone: 'Europe/Paris',
      });
      await fixture.componentInstance.submit();

      expect(triggerService.create).toHaveBeenCalledWith('p1', {
        agentId: 'a1',
        name: 'Rapport',
        type: 'cron',
        cronExpression: '0 9 * * 1-5',
        timeZone: 'Europe/Paris',
      });
    });

    it('reveals the secret and keeps it until it is dismissed', async () => {
      setup();
      agentService.agents.set([agent({ id: 'a1', isPublished: true })]);
      openForm();
      fixture.componentInstance.form.patchValue({ name: 'Push', agentId: 'a1' });

      await fixture.componentInstance.submit();
      fixture.detectChanges();

      expect(el('secret-value')!.textContent).toContain('sh-secret-value');
      expect(el('hook-url')!.textContent).toContain('/api/hooks/t1');

      // Il ne repassera plus : le faire disparaître tout seul le perdrait, et il n'y a pas de
      // « secret oublié » pour un webhook.
      el('dismiss-secret')!.click();
      fixture.detectChanges();
      expect(el('revealed-secret')).toBeNull();
    });

    it('shows the server reason rather than a generic failure', async () => {
      setup();
      agentService.agents.set([agent({ id: 'a1', isPublished: true })]);
      openForm();
      fixture.componentInstance.form.patchValue({ name: 'X', agentId: 'a1' });
      triggerService.create.mockRejectedValueOnce({ error: { error: "Unrecognized value 'lundi'" } });

      await fixture.componentInstance.submit();
      fixture.detectChanges();

      // C'est le seul message qui dit quel champ de l'expression est fautif.
      expect(el('submit-error')!.textContent).toContain("Unrecognized value 'lundi'");
    });

    it('falls back to a translation key when the server says nothing useful', async () => {
      setup();
      agentService.agents.set([agent({ id: 'a1', isPublished: true })]);
      openForm();
      fixture.componentInstance.form.patchValue({ name: 'X', agentId: 'a1' });
      triggerService.create.mockRejectedValueOnce(new Error('network'));

      await fixture.componentInstance.submit();
      fixture.detectChanges();

      expect(el('submit-error')!.textContent).toContain('errors.createTrigger');
    });

    it('does not submit an incomplete form', async () => {
      setup();
      openForm();
      // Ni nom ni agent : le serveur refuserait, et le lui demander n'apprendrait rien.
      await fixture.componentInstance.submit();

      expect(triggerService.create).not.toHaveBeenCalled();
    });
  });

  describe('row actions', () => {
    it('toggles a trigger between active and disabled', async () => {
      setup();
      triggerService.triggers.set([trigger({ id: 't1', isActive: true })]);
      fixture.detectChanges();

      el('toggle-trigger')!.click();
      await fixture.whenStable();

      expect(triggerService.update).toHaveBeenCalledWith('t1', { isActive: false });
    });

    it('deletes a trigger', async () => {
      setup();
      triggerService.triggers.set([trigger({ id: 't1' })]);
      fixture.detectChanges();

      el('delete-trigger')!.click();
      await fixture.whenStable();

      expect(triggerService.remove).toHaveBeenCalledWith('t1');
    });

    it('marks a disabled trigger as such', () => {
      setup();
      triggerService.triggers.set([trigger({ id: 't1', isActive: false })]);
      fixture.detectChanges();

      expect(el('trigger-inactive')).not.toBeNull();
    });

    it('shows the cron schedule and its next occurrence', () => {
      setup();
      triggerService.triggers.set([
        trigger({
          id: 't1',
          type: 'cron',
          cronExpression: '0 9 * * *',
          timeZone: 'Europe/Paris',
          nextRunAt: '2031-03-03T08:00:00Z',
          webhookPath: null,
        }),
      ]);
      fixture.detectChanges();

      const row = el('trigger-row')!;
      expect(row.textContent).toContain('0 9 * * *');
      expect(row.textContent).toContain('Europe/Paris');
      expect(el('trigger-next-run')).not.toBeNull();
      // Un déclencheur planifié n'a pas d'URL : en afficher une serait un contresens.
      expect(el('trigger-hook-path')).toBeNull();
    });
  });

  describe('role gating', () => {
    it.each<UserRole>(['developer', 'viewer'])(
      'offers a %s no action at all, because the server refuses them all',
      (role) => {
        setup(role);
        triggerService.triggers.set([trigger({ id: 't1' })]);
        fixture.detectChanges();

        // La lecture n'engage rien et reste ouverte ; poser un déclencheur, c'est donner à un
        // tiers le droit de dépenser le budget du projet.
        expect(all('trigger-row').length).toBe(1);
        expect(el('new-trigger')).toBeNull();
        expect(el('toggle-trigger')).toBeNull();
        expect(el('delete-trigger')).toBeNull();
      },
    );

    it.each<UserRole>(['owner', 'maintainer'])('offers a %s the full set of actions', (role) => {
      setup(role);
      triggerService.triggers.set([trigger({ id: 't1' })]);
      fixture.detectChanges();

      expect(el('new-trigger')).not.toBeNull();
      expect(el('toggle-trigger')).not.toBeNull();
      expect(el('delete-trigger')).not.toBeNull();
    });

    it('refuses to submit even if the form is reached by another route', async () => {
      setup('viewer');
      fixture.componentInstance.form.patchValue({ name: 'X', agentId: 'a1' });

      await fixture.componentInstance.submit();

      // Le serveur reste l'autorité ; c'est de la défense en profondeur, pas la barrière.
      expect(triggerService.create).not.toHaveBeenCalled();
    });
  });
});
