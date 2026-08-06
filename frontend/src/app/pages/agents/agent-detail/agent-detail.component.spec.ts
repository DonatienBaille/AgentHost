import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { AgentDetailComponent } from './agent-detail.component';
import { AgentService } from '../../../services/agent.service';
import { AuthService } from '../../../services/auth.service';
import { Agent, AgentVersion, UserRole } from '../../../core/models';
import { agent, agentVersion, user } from '../../../core/testing/fixtures';

/**
 * Double du service : mêmes signaux que le vrai, aucun HTTP.
 *
 * `fetchAgent` renvoie `null` en cas d'échec, exactement comme le vrai service qui avale
 * l'erreur HTTP (services/agent.service.ts) — c'est ce contrat-là que la page doit gérer.
 */
class AgentServiceStub {
  readonly agents = signal<Agent[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly versions = signal<AgentVersion[]>([]);
  readonly isLoadingVersions = signal(false);
  readonly fetchAgent = vi.fn(async (id: string): Promise<Agent | null> => agent({ id }));
  readonly listVersions = vi.fn(async (_agentId: string): Promise<AgentVersion[]> => []);
  readonly publishVersion = vi.fn(
    async (agentId: string, manifestYaml: string): Promise<AgentVersion> =>
      agentVersion({ agentId, manifestYaml, versionNumber: 2, id: 'v2' }),
  );
}

describe('AgentDetailComponent', () => {
  let fixture: ComponentFixture<AgentDetailComponent>;
  let component: AgentDetailComponent;
  let agentService: AgentServiceStub;
  let authService: AuthService;

  /** Construit le composant puis laisse le chargement asynchrone se terminer. */
  async function setup(
    role: UserRole | null = 'developer',
    params: Record<string, string> = { id: 'ag1' },
    configure: (s: AgentServiceStub) => void = () => {},
  ): Promise<void> {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [AgentDetailComponent],
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
    configure(agentService);

    fixture = TestBed.createComponent(AgentDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await settle();
  }

  /** Vide la file des microtâches puis rafraîchit la vue. */
  async function settle(): Promise<void> {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
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

  describe('loading the agent', () => {
    it('fetches the agent and its versions from the route id', async () => {
      await setup('developer', { id: 'ag42' });

      expect(agentService.fetchAgent).toHaveBeenCalledWith('ag42');
      expect(agentService.listVersions).toHaveBeenCalledWith('ag42');
      expect(component.isLoading()).toBe(false);
    });

    it('does nothing at all when the route carries no id', async () => {
      await setup('developer', {});

      expect(agentService.fetchAgent).not.toHaveBeenCalled();
      expect(agentService.listVersions).not.toHaveBeenCalled();
      expect(el('agent-name')).toBeNull();
    });

    it('renders the header, the subtitle and the manifest of the loaded agent', async () => {
      await setup('developer', { id: 'ag1' }, (s) => {
        s.fetchAgent.mockResolvedValue(
          agent({
            id: 'ag1',
            name: 'Rédacteur',
            slug: 'redacteur',
            agentType: 'claude_code',
            manifestYaml: 'name: redacteur\nversion: 3',
          }),
        );
      });

      expect(el('agent-name')!.textContent!.trim()).toBe('Rédacteur');
      expect(el('agent-subtitle')!.textContent).toContain('redacteur');
      expect(el('agent-subtitle')!.textContent).toContain('claude_code');
      expect(el('agent-manifest')!.textContent).toBe('name: redacteur\nversion: 3');
    });

    it('badges a published agent', async () => {
      await setup('developer', { id: 'ag1' }, (s) => {
        s.fetchAgent.mockResolvedValue(agent({ isPublished: true }));
      });

      expect(el('agent-published')).not.toBeNull();
    });

    it('shows no badge for a draft agent', async () => {
      await setup('developer', { id: 'ag1' }, (s) => {
        s.fetchAgent.mockResolvedValue(agent({ isPublished: false }));
      });

      expect(el('agent-name')).not.toBeNull();
      expect(el('agent-published')).toBeNull();
    });

    it('prefills the publish form with the current manifest', async () => {
      await setup('developer', { id: 'ag1' }, (s) => {
        s.fetchAgent.mockResolvedValue(agent({ manifestYaml: 'name: a\nversion: 7' }));
      });

      expect(component.publishForm.getRawValue().manifestYaml).toBe('name: a\nversion: 7');
    });
  });

  describe('missing agent', () => {
    // Le service renvoyait `null` sur un 404 en s'étant contenté de poser son propre signal
    // d'erreur ; le `catch` de `load()` ne s'exécutait donc jamais et la page rendait un corps
    // entièrement vide — ni agent, ni erreur, ni « introuvable ». Le service lève désormais.
    it('says the agent could not be loaded instead of rendering a blank page', async () => {
      await setup('developer', { id: 'nope' }, (s) => {
        s.fetchAgent.mockRejectedValue(new Error('404'));
      });

      expect(component.agent()).toBeNull();
      expect(component.loadError()).toBe('agentDetail.loadError');
      expect(el('agent-load-error')).not.toBeNull();
      expect(el('agent-name')).toBeNull();
      expect(el('publish-panel')).toBeNull();
    });

    it('leaves the publish form empty when there is no agent to prefill it from', async () => {
      await setup('developer', { id: 'nope' }, (s) => {
        s.fetchAgent.mockRejectedValue(new Error('404'));
      });

      expect(component.publishForm.getRawValue().manifestYaml).toBe('');
    });

    it('does not ask for the versions of an agent it could not load', async () => {
      await setup('developer', { id: 'nope' }, (s) => {
        s.fetchAgent.mockRejectedValue(new Error('404'));
      });

      // Un second appel voué à échouer, dont la seule conséquence visible serait une deuxième
      // bulle d'erreur pour la même cause.
      expect(agentService.listVersions).not.toHaveBeenCalled();
    });

    it('drops the previously loaded agent when navigating to an unknown id', async () => {
      await setup('developer', { id: 'ag1' });
      expect(component.agent()).not.toBeNull();

      agentService.fetchAgent.mockRejectedValue(new Error('404'));
      await component.ngOnInit();
      await Promise.resolve();
      fixture.detectChanges();

      // Garder l'ancien afficherait le mauvais agent sous un message d'erreur.
      expect(component.agent()).toBeNull();
      expect(el('agent-name')).toBeNull();
    });

    it('shows the load error when the fetch rejects outright', async () => {
      await setup('developer', { id: 'ag1' }, (s) => {
        s.fetchAgent.mockRejectedValue(new Error('boom'));
      });

      expect(component.loadError()).toBe('agentDetail.loadError');
      expect(el('agent-load-error')!.textContent).toContain('agentDetail.loadError');
      expect(el('agent-name')).toBeNull();
    });

    it('keeps rendering the agent when only the versions fail to load', async () => {
      await setup('developer', { id: 'ag1' }, (s) => {
        s.listVersions.mockRejectedValue(new Error('boom'));
      });

      expect(el('agent-name')).not.toBeNull();
      expect(component.loadError()).toBeNull();
      expect(el('versions-empty')).not.toBeNull();
    });
  });

  describe('versions', () => {
    it('shows the empty state and no row when the agent has no version yet', async () => {
      await setup();

      expect(all('version-row').length).toBe(0);
      expect(el('versions-empty')).not.toBeNull();
    });

    it('hides the empty state while the versions load, so it never flashes', async () => {
      await setup();
      agentService.isLoadingVersions.set(true);
      fixture.detectChanges();

      expect(el('versions-empty')).toBeNull();
      expect(fixture.nativeElement.querySelector('.animate-spin')).not.toBeNull();
    });

    it('renders one row per version with its number and digest', async () => {
      await setup();
      agentService.versions.set([
        agentVersion({ id: 'v2', versionNumber: 2, digestSha256: 'sha256:beef' }),
        agentVersion({ id: 'v1', versionNumber: 1, digestSha256: 'sha256:cafe' }),
      ]);
      fixture.detectChanges();

      const rows = all('version-row');
      expect(rows.length).toBe(2);
      expect(el('versions-empty')).toBeNull();
      expect(rows[0].textContent).toContain('v2');
      expect(rows[0].textContent).toContain('sha256:beef');
      expect(rows[1].textContent).toContain('v1');
    });

    it('keeps the service order as-is, without re-sorting', async () => {
      await setup();
      agentService.versions.set([
        agentVersion({ id: 'v1', versionNumber: 1 }),
        agentVersion({ id: 'v3', versionNumber: 3 }),
        agentVersion({ id: 'v2', versionNumber: 2 }),
      ]);
      fixture.detectChanges();

      const numbers = [...all('version-row')].map((r) => r.querySelector('span')!.textContent);
      expect(numbers).toEqual(['v1', 'v3', 'v2']);
    });
  });

  describe('role gating', () => {
    it.each<UserRole>(['developer', 'maintainer', 'owner'])(
      'shows the publish panel to a %s',
      async (role) => {
        await setup(role);

        expect(component.canPublish()).toBe(true);
        expect(el('publish-panel')).not.toBeNull();
      },
    );

    it('hides the publish panel from a viewer', async () => {
      await setup('viewer');

      expect(component.canPublish()).toBe(false);
      expect(el('publish-panel')).toBeNull();
      expect(el('publish-submit')).toBeNull();
    });

    it('hides the publish panel from a signed-out user', async () => {
      await setup(null);

      expect(component.canPublish()).toBe(false);
      expect(el('publish-panel')).toBeNull();
    });

    it('still shows the agent and its versions to a viewer: reading is allowed', async () => {
      await setup('viewer');
      agentService.versions.set([agentVersion()]);
      fixture.detectChanges();

      expect(el('agent-name')).not.toBeNull();
      expect(all('version-row').length).toBe(1);
    });

    it('refuses a forced publish from a viewer', async () => {
      await setup('viewer');
      component.publishForm.setValue({ manifestYaml: 'name: a' });

      // Masquer le panneau n'empêche pas d'atteindre la méthode. Le serveur reste l'autorité —
      // cette garde est de la défense en profondeur.
      await component.publish();

      expect(agentService.publishVersion).not.toHaveBeenCalled();
    });
  });

  describe('publishing a new version', () => {
    it('sends the agent id and the manifest, and nothing else', async () => {
      await setup('developer', { id: 'ag42' });
      component.publishForm.setValue({ manifestYaml: 'name: a\nversion: 2' });

      await component.publish();

      expect(agentService.publishVersion).toHaveBeenCalledTimes(1);
      expect(agentService.publishVersion).toHaveBeenCalledWith('ag42', 'name: a\nversion: 2');
    });

    it('sends the manifest verbatim, newlines included', async () => {
      await setup();
      const manifest = 'name: a\nsteps:\n  - run: echo "hi"\n';
      component.publishForm.setValue({ manifestYaml: manifest });

      await component.publish();

      expect(agentService.publishVersion).toHaveBeenCalledWith('ag1', manifest);
    });

    it('reloads the agent and its versions after a successful publish', async () => {
      await setup('developer', { id: 'ag1' });
      agentService.fetchAgent.mockClear();
      agentService.listVersions.mockClear();
      component.publishForm.setValue({ manifestYaml: 'name: a' });

      await component.publish();

      expect(agentService.fetchAgent).toHaveBeenCalledWith('ag1');
      expect(agentService.listVersions).toHaveBeenCalledWith('ag1');
      expect(component.isPublishing()).toBe(false);
      expect(component.publishError()).toBeNull();
    });

    it('refreshes the displayed manifest with the one the server kept', async () => {
      await setup('developer', { id: 'ag1' }, (s) => {
        s.fetchAgent.mockResolvedValue(agent({ manifestYaml: 'name: a\nversion: 1' }));
      });
      agentService.fetchAgent.mockResolvedValue(agent({ manifestYaml: 'name: a\nversion: 2' }));
      component.publishForm.setValue({ manifestYaml: 'name: a\nversion: 2' });

      await component.publish();
      fixture.detectChanges();

      expect(el('agent-manifest')!.textContent).toBe('name: a\nversion: 2');
    });

    it('does not publish an empty manifest', async () => {
      await setup();
      component.publishForm.setValue({ manifestYaml: '' });

      await component.publish();

      expect(agentService.publishVersion).not.toHaveBeenCalled();
      expect(component.publishForm.controls.manifestYaml.touched).toBe(true);
    });

    it('surfaces a publish failure without clearing the manifest', async () => {
      await setup();
      agentService.publishVersion.mockRejectedValueOnce(new Error('boom'));
      component.publishForm.setValue({ manifestYaml: 'name: a\nversion: 2' });

      await component.publish();
      fixture.detectChanges();

      expect(component.publishError()).toBe('agentDetail.publishError');
      expect(el('publish-error')!.textContent).toContain('agentDetail.publishError');
      expect(component.publishForm.getRawValue().manifestYaml).toBe('name: a\nversion: 2');
      expect(component.isPublishing()).toBe(false);
    });

    it('does not reload the agent when the publish failed', async () => {
      await setup();
      agentService.fetchAgent.mockClear();
      agentService.publishVersion.mockRejectedValueOnce(new Error('boom'));
      component.publishForm.setValue({ manifestYaml: 'name: a' });

      await component.publish();

      expect(agentService.fetchAgent).not.toHaveBeenCalled();
    });

    it('clears a previous error on the next successful publish', async () => {
      await setup();
      agentService.publishVersion.mockRejectedValueOnce(new Error('boom'));
      component.publishForm.setValue({ manifestYaml: 'name: a' });
      await component.publish();
      expect(component.publishError()).toBe('agentDetail.publishError');

      component.publishForm.setValue({ manifestYaml: 'name: a\nversion: 2' });
      await component.publish();

      expect(component.publishError()).toBeNull();
    });

    it('disables the submit button while the publish is in flight', async () => {
      await setup();
      component.isPublishing.set(true);
      fixture.detectChanges();

      expect((el('publish-submit') as HTMLButtonElement).disabled).toBe(true);
    });
  });
});
