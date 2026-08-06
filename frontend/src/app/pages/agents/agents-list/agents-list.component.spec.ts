import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { AgentsListComponent } from './agents-list.component';
import { AgentService } from '../../../services/agent.service';
import { AuthService } from '../../../services/auth.service';
import { Agent, CreateAgentRequest, UserRole } from '../../../core/models';
import { agent, user } from '../../../core/testing/fixtures';

const MANIFEST = 'name: redacteur\nversion: 1\ninputs: {}';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class AgentServiceStub {
  readonly agents = signal<Agent[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listAgents = vi.fn(async (_projectId?: string) => {});
  readonly createAgent = vi.fn(async (req: CreateAgentRequest) =>
    agent({ name: req.name, slug: req.slug, projectId: req.projectId }),
  );
}

describe('AgentsListComponent', () => {
  let fixture: ComponentFixture<AgentsListComponent>;
  let component: AgentsListComponent;
  let agentService: AgentServiceStub;
  let authService: AuthService;

  function setup(
    role: UserRole | null = 'developer',
    params: Record<string, string> = { id: 'p1' },
  ): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [AgentsListComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: AgentService, useClass: AgentServiceStub },
        { provide: ActivatedRoute, useValue: { params: of(params) } },
      ],
    });

    agentService = TestBed.inject(AgentService) as unknown as AgentServiceStub;
    authService = TestBed.inject(AuthService);
    authService.currentUser.set(role ? user(role) : null);

    fixture = TestBed.createComponent(AgentsListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function all(testId: string): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`);
  }

  /** Remplit le formulaire avec un jeu valide, surchargé au besoin. */
  function fillForm(overrides: Partial<Record<string, unknown>> = {}): void {
    component.form.setValue({
      name: 'Rédacteur',
      slug: 'redacteur',
      manifestYaml: MANIFEST,
      publish: true,
      ...overrides,
    } as never);
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('scoping by route', () => {
    it('scopes the listing to the project of the route', () => {
      setup('developer', { id: 'p42' });

      expect(component.projectId()).toBe('p42');
      expect(agentService.listAgents).toHaveBeenCalledWith('p42');
      expect(component.pageTitle()).toBe('agentsList.titleForProject');
    });

    it('lists every agent of the org when the route carries no project', () => {
      setup('developer', {});

      expect(component.projectId()).toBeNull();
      expect(agentService.listAgents).toHaveBeenCalledWith(undefined);
      expect(component.pageTitle()).toBe('agentsList.title');
    });

    it('renders the title matching the scope', () => {
      setup('developer', {});

      // Clés non résolues en test : ngx-translate rend la clé telle quelle.
      expect(fixture.nativeElement.querySelector('h1')!.textContent!.trim()).toBe(
        'agentsList.title',
      );
    });
  });

  describe('list states', () => {
    it('shows the empty state and no card when the list comes back empty', () => {
      setup();

      expect(all('agent-card').length).toBe(0);
      expect(el('agents-empty')).not.toBeNull();
    });

    it('hides the empty state while loading, so it never flashes', () => {
      setup();
      agentService.isLoading.set(true);
      fixture.detectChanges();

      expect(el('agents-empty')).toBeNull();
      expect(fixture.nativeElement.querySelector('.animate-spin')).not.toBeNull();
    });

    it('renders the error signal of the service', () => {
      setup();
      agentService.error.set('Failed to load agents');
      fixture.detectChanges();

      const banner = el('agents-error');
      expect(banner).not.toBeNull();
      expect(banner!.textContent).toContain('Failed to load agents');
    });

    it('renders one card per agent with the displayed fields and its link', () => {
      setup();
      agentService.agents.set([
        agent({ id: 'ag1', name: 'Rédacteur', slug: 'redacteur', agentType: 'oci' }),
        agent({ id: 'ag2', name: 'Relecteur', slug: 'relecteur', agentType: 'claude_code' }),
      ]);
      fixture.detectChanges();

      const cards = all('agent-card');
      expect(cards.length).toBe(2);
      expect(el('agents-empty')).toBeNull();

      expect(cards[0].textContent).toContain('Rédacteur');
      expect(cards[0].textContent).toContain('redacteur');
      expect(cards[0].textContent).toContain('oci');
      expect(cards[0].getAttribute('href')).toBe('/agents/ag1');

      expect(cards[1].textContent).toContain('claude_code');
      expect(cards[1].getAttribute('href')).toBe('/agents/ag2');
    });

    it('badges each agent as published or draft, never both', () => {
      setup();
      agentService.agents.set([
        agent({ id: 'ag1', isPublished: true }),
        agent({ id: 'ag2', isPublished: false }),
      ]);
      fixture.detectChanges();

      const cards = all('agent-card');
      expect(cards[0].querySelector('[data-testid="agent-published"]')).not.toBeNull();
      expect(cards[0].querySelector('[data-testid="agent-draft"]')).toBeNull();
      expect(cards[1].querySelector('[data-testid="agent-draft"]')).not.toBeNull();
      expect(cards[1].querySelector('[data-testid="agent-published"]')).toBeNull();
    });

    it('links to the global agent route, not to a project-scoped one', () => {
      setup('developer', { id: 'p42' });
      agentService.agents.set([agent({ id: 'ag1', projectId: 'p42' })]);
      fixture.detectChanges();

      expect(all('agent-card')[0].getAttribute('href')).toBe('/agents/ag1');
    });
  });

  describe('role gating', () => {
    it.each<UserRole>(['developer', 'maintainer', 'owner'])(
      'shows the create button to a %s inside a project',
      (role) => {
        setup(role, { id: 'p1' });

        expect(component.canCreate()).toBe(true);
        expect(el('new-agent')).not.toBeNull();
      },
    );

    it('hides the create button from a viewer', () => {
      setup('viewer', { id: 'p1' });

      expect(component.canCreate()).toBe(false);
      expect(el('new-agent')).toBeNull();
      expect(el('agent-form')).toBeNull();
    });

    it('hides the create button from a signed-out user', () => {
      setup(null, { id: 'p1' });

      expect(component.canCreate()).toBe(false);
      expect(el('new-agent')).toBeNull();
    });

    it('hides the create button on the global list even from an owner', () => {
      setup('owner', {});

      // Un agent appartient toujours à un projet : sans projectId, rien à créer.
      expect(component.canCreate()).toBe(true);
      expect(el('new-agent')).toBeNull();
    });

    it('still lists the agents for a viewer: reading is allowed', () => {
      setup('viewer');
      agentService.agents.set([agent()]);
      fixture.detectChanges();

      expect(all('agent-card').length).toBe(1);
      expect(agentService.listAgents).toHaveBeenCalledTimes(1);
    });

    it('reveals the form only once the create button is clicked', () => {
      setup('developer');
      expect(el('agent-form')).toBeNull();

      el('new-agent')!.click();
      fixture.detectChanges();

      expect(el('agent-form')).not.toBeNull();
    });
  });

  describe('create payload', () => {
    it('sends exactly the five fields of CreateAgentRequest', async () => {
      setup('developer', { id: 'p42' });
      fillForm();

      await component.submit();

      expect(agentService.createAgent).toHaveBeenCalledWith({
        projectId: 'p42',
        name: 'Rédacteur',
        slug: 'redacteur',
        manifestYaml: MANIFEST,
        publish: true,
      });
      // Ni orgId ni schémas : le serveur les dérive du manifeste (CreateAgentRequest).
      const sent = agentService.createAgent.mock.calls[0][0] as unknown as Record<string, unknown>;
      expect(Object.keys(sent).sort()).toEqual([
        'manifestYaml',
        'name',
        'projectId',
        'publish',
        'slug',
      ]);
    });

    it('takes the projectId from the route, never from the form', async () => {
      setup('developer', { id: 'p42' });
      fillForm();

      await component.submit();

      expect(agentService.createAgent).toHaveBeenCalledWith(
        expect.objectContaining({ projectId: 'p42' }),
      );
      expect(Object.keys(component.form.getRawValue())).not.toContain('projectId');
    });

    it('sends the manifest verbatim, newlines included', async () => {
      setup('developer');
      const manifest = 'name: a\nsteps:\n  - run: echo "hi"\n';
      fillForm({ manifestYaml: manifest });

      await component.submit();

      expect(agentService.createAgent).toHaveBeenCalledWith(
        expect.objectContaining({ manifestYaml: manifest }),
      );
    });

    it('sends publish false when the box is unchecked', async () => {
      setup('developer');
      fillForm({ publish: false });

      await component.submit();

      expect(agentService.createAgent).toHaveBeenCalledWith(
        expect.objectContaining({ publish: false }),
      );
    });

    it('defaults publish to true when the control was cleared to null', async () => {
      setup('developer');
      fillForm({ publish: null });

      await component.submit();

      expect(agentService.createAgent).toHaveBeenCalledWith(
        expect.objectContaining({ publish: true }),
      );
    });

    it('closes the form and resets it with publish back to true after a success', async () => {
      setup('developer');
      component.toggleForm();
      fillForm({ publish: false });

      await component.submit();

      expect(component.showForm()).toBe(false);
      expect(component.form.getRawValue().publish).toBe(true);
      expect(component.form.getRawValue().manifestYaml).toBeNull();
      expect(component.submitError()).toBeNull();
      expect(component.isSubmitting()).toBe(false);
    });
  });

  describe('create validation', () => {
    it('refuses to create anything on the global list, with no project to attach to', async () => {
      setup('developer', {});
      fillForm();

      await component.submit();

      expect(agentService.createAgent).not.toHaveBeenCalled();
    });

    it('does not call the service when the manifest is missing', async () => {
      setup('developer');
      fillForm({ manifestYaml: '' });

      await component.submit();

      expect(agentService.createAgent).not.toHaveBeenCalled();
      expect(component.form.controls.manifestYaml.touched).toBe(true);
    });

    it('does not call the service when the name is missing', async () => {
      setup('developer');
      fillForm({ name: '' });

      await component.submit();

      expect(agentService.createAgent).not.toHaveBeenCalled();
    });

    it.each(['Redacteur', 'redacteur_v2', 'redacteur v2', 'rédacteur'])(
      'refuses the non-kebab slug %s',
      async (slug) => {
        setup('developer');
        fillForm({ slug });

        await component.submit();

        expect(agentService.createAgent).not.toHaveBeenCalled();
      },
    );

    it('accepts a kebab-case slug with digits', async () => {
      setup('developer');
      fillForm({ slug: 'redacteur-v2' });

      await component.submit();

      expect(agentService.createAgent).toHaveBeenCalledWith(
        expect.objectContaining({ slug: 'redacteur-v2' }),
      );
    });
  });

  describe('create failure', () => {
    it('surfaces the error and keeps the form open with its values', async () => {
      setup('developer');
      component.toggleForm();
      agentService.createAgent.mockRejectedValueOnce(new Error('boom'));
      fillForm();

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(true);
      expect(component.isSubmitting()).toBe(false);
      expect(component.form.getRawValue().manifestYaml).toBe(MANIFEST);
      expect(el('agent-submit-error')!.textContent).toContain('agentsList.createError');
    });

    it('clears a previous error on the next successful submit', async () => {
      setup('developer');
      agentService.createAgent.mockRejectedValueOnce(new Error('boom'));
      fillForm();
      await component.submit();
      expect(component.submitError()).toBe('agentsList.createError');

      fillForm();
      await component.submit();

      expect(component.submitError()).toBeNull();
    });
  });
});
