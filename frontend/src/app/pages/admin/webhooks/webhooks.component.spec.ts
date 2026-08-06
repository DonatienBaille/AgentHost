import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { WebhooksComponent } from './webhooks.component';
import { WebhookService } from '../../../services/webhook.service';
import { AuthService } from '../../../services/auth.service';
import {
  CreateWebhookRequest,
  UpdateWebhookRequest,
  UserRole,
  WEBHOOK_EVENTS,
  Webhook,
} from '../../../core/models';
import { user, webhook } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class WebhookServiceStub {
  readonly webhooks = signal<Webhook[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listWebhooks = vi.fn(async (_projectId: string) => {});
  readonly createWebhook = vi.fn(async (req: CreateWebhookRequest) =>
    webhook({ url: req.url, events: req.events }),
  );
  readonly updateWebhook = vi.fn(async (id: string, _req: UpdateWebhookRequest) => webhook({ id }));
  readonly toggleActive = vi.fn(async (w: Webhook) => {
    await this.updateWebhook(w.id, { isActive: !w.isActive });
  });
  readonly deleteWebhook = vi.fn(async (_id: string) => {});
}

describe('WebhooksComponent', () => {
  let fixture: ComponentFixture<WebhooksComponent>;
  let component: WebhooksComponent;
  let webhookService: WebhookServiceStub;
  let authService: AuthService;

  function setup(role: UserRole | null = 'owner', params: Record<string, string> = { projectId: 'p1' }): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [WebhooksComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: WebhookService, useClass: WebhookServiceStub },
        { provide: ActivatedRoute, useValue: { params: of(params) } },
      ],
    });

    webhookService = TestBed.inject(WebhookService) as unknown as WebhookServiceStub;
    authService = TestBed.inject(AuthService);
    authService.currentUser.set(role ? user(role) : null);

    fixture = TestBed.createComponent(WebhooksComponent);
    component = fixture.componentInstance;
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

  describe('list states', () => {
    it('loads the webhooks of the routed project', () => {
      setup('owner', { projectId: 'p42' });

      expect(component.projectId()).toBe('p42');
      expect(webhookService.listWebhooks).toHaveBeenCalledWith('p42');
    });

    it('loads nothing when the route carries no project id', () => {
      setup('owner', {});

      expect(component.projectId()).toBe('');
      expect(webhookService.listWebhooks).not.toHaveBeenCalled();
    });

    it('shows the empty state and no cards when the list comes back empty', () => {
      setup();

      expect(all('webhook-row').length).toBe(0);
      expect(el('webhooks-empty')).not.toBeNull();
    });

    it('hides the empty state while loading', () => {
      setup();
      webhookService.isLoading.set(true);
      fixture.detectChanges();

      expect(el('webhooks-empty')).toBeNull();
    });

    it('renders the service error message', () => {
      setup();
      webhookService.error.set('errors.loadWebhooks');
      fixture.detectChanges();

      expect(el('webhooks-error')!.textContent).toContain('errors.loadWebhooks');
    });

    it('renders one card per webhook with url, events and active state', () => {
      setup();
      webhookService.webhooks.set([
        webhook({
          id: 'w1',
          url: 'https://a.example/hook',
          events: ['run.succeeded', 'run.failed'],
          isActive: true,
        }),
        webhook({ id: 'w2', url: 'https://b.example/hook', events: [], isActive: false }),
      ]);
      fixture.detectChanges();

      const cards = all('webhook-row');
      expect(cards.length).toBe(2);
      expect(el('webhooks-empty')).toBeNull();

      expect(cards[0].querySelector('[data-testid="webhook-url"]')!.textContent!.trim()).toBe(
        'https://a.example/hook',
      );
      const events = cards[0].querySelectorAll('[data-testid="webhook-event"]');
      expect([...events].map((e) => e.textContent!.trim())).toEqual([
        'run.succeeded',
        'run.failed',
      ]);
      // Clés non résolues en test : ngx-translate rend la clé telle quelle.
      expect(
        cards[0].querySelector('[data-testid="webhook-status"]')!.textContent!.trim(),
      ).toBe('adminWebhooks.active');
      expect(
        cards[0].querySelector('[data-testid="toggle-webhook"]')!.textContent!.trim(),
      ).toBe('adminWebhooks.deactivate');

      expect(cards[1].querySelectorAll('[data-testid="webhook-event"]').length).toBe(0);
      expect(
        cards[1].querySelector('[data-testid="webhook-status"]')!.textContent!.trim(),
      ).toBe('adminWebhooks.inactive');
      expect(
        cards[1].querySelector('[data-testid="toggle-webhook"]')!.textContent!.trim(),
      ).toBe('adminWebhooks.activate');
    });

    it('offers a checkbox for every canonical event', () => {
      setup();
      component.toggleForm();
      fixture.detectChanges();

      const boxes = fixture.nativeElement.querySelectorAll('input[type="checkbox"]');
      expect(boxes.length).toBe(WEBHOOK_EVENTS.length);
    });
  });

  describe('role gating', () => {
    /**
     * Le serveur exige `maintainer` sur POST/PUT/DELETE /api/webhooks. La page n'injectait même
     * pas AuthService : créer, (dés)activer et supprimer un webhook — donc rediriger les
     * événements d'un projet vers une URL arbitraire — était offert à un viewer, qui ne récoltait
     * qu'un 403 après avoir rempli le formulaire.
     */
    it.each<UserRole>(['owner', 'maintainer'])('offers every action to a %s', (role) => {
      setup(role);
      webhookService.webhooks.set([webhook()]);
      fixture.detectChanges();

      expect(el('new-webhook')).not.toBeNull();
      expect(all('toggle-webhook').length).toBe(1);
      expect(all('delete-webhook').length).toBe(1);
      expect(el('webhooks-read-only')).toBeNull();
    });

    it.each<UserRole>(['developer', 'viewer'])(
      'gives a %s a read-only page, with a reason',
      (role) => {
        setup(role);
        webhookService.webhooks.set([webhook()]);
        fixture.detectChanges();

        expect(el('new-webhook')).toBeNull();
        expect(all('toggle-webhook').length).toBe(0);
        expect(all('delete-webhook').length).toBe(0);
        // La liste reste visible — la lecture est permise — mais la page dit pourquoi elle est inerte.
        expect(all('webhook-row').length).toBe(1);
        expect(el('webhooks-read-only')).not.toBeNull();
      },
    );

    it('does not render the create form for a viewer, even if the signal is forced', () => {
      setup('viewer');

      component.toggleForm();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('#w-url')).toBeNull();
      expect(fixture.nativeElement.querySelector('#w-secret')).toBeNull();
    });

    it('refuses a forced create, toggle or delete from an under-privileged user', async () => {
      setup('viewer');
      const existing = webhook();
      webhookService.webhooks.set([existing]);
      fixture.detectChanges();

      component.form.patchValue({ url: 'https://evil.example/hook' });
      await component.submit();
      await component.toggleActive(existing);
      await component.remove(existing.id);

      expect(webhookService.createWebhook).not.toHaveBeenCalled();
      expect(webhookService.updateWebhook).not.toHaveBeenCalled();
      expect(webhookService.deleteWebhook).not.toHaveBeenCalled();
    });
  });

  describe('create payload', () => {
    function fillEvents(selected: string[]): void {
      const events = Object.fromEntries(WEBHOOK_EVENTS.map((e) => [e, selected.includes(e)]));
      component.form.controls.events.setValue(events);
    }

    it('sends the routed project id, the url and only the ticked events', async () => {
      setup('owner', { projectId: 'p42' });
      component.form.patchValue({ url: 'https://a.example/hook', secretToken: 'tok' });
      fillEvents(['run.created', 'run.failed']);

      await component.submit();

      expect(webhookService.createWebhook).toHaveBeenCalledTimes(1);
      expect(webhookService.createWebhook).toHaveBeenCalledWith({
        projectId: 'p42',
        url: 'https://a.example/hook',
        events: ['run.created', 'run.failed'],
        secretToken: 'tok',
      });
      const sent = webhookService.createWebhook.mock.calls[0][0] as unknown as Record<
        string,
        unknown
      >;
      expect(Object.keys(sent).sort()).toEqual(['events', 'projectId', 'secretToken', 'url']);
    });

    it('omits an empty secret token rather than sending an empty string', async () => {
      setup('owner');
      component.form.patchValue({ url: 'https://a.example/hook', secretToken: '' });
      fillEvents(['run.finished']);

      await component.submit();

      expect(webhookService.createWebhook).toHaveBeenCalledWith(
        expect.objectContaining({ secretToken: undefined, events: ['run.finished'] }),
      );
    });

    it('refuses to submit with no event ticked', async () => {
      setup('owner');
      component.form.patchValue({ url: 'https://a.example/hook' });
      fillEvents([]);

      await component.submit();

      expect(webhookService.createWebhook).not.toHaveBeenCalled();
      expect(component.form.touched).toBe(true);
    });

    it('refuses to submit with no url', async () => {
      setup('owner');
      fillEvents(['run.created']);

      await component.submit();

      expect(webhookService.createWebhook).not.toHaveBeenCalled();
    });

    it('refuses to submit when the route carried no project id', async () => {
      setup('owner', {});
      component.form.patchValue({ url: 'https://a.example/hook' });
      fillEvents(['run.created']);

      await component.submit();

      expect(webhookService.createWebhook).not.toHaveBeenCalled();
    });

    it('closes the form after a successful create', async () => {
      setup('owner');
      component.toggleForm();
      component.form.patchValue({ url: 'https://a.example/hook' });
      fillEvents(['run.created']);

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(false);
      expect(fixture.nativeElement.querySelector('#w-url')).toBeNull();
    });

    it('surfaces a create failure without closing the form', async () => {
      setup('owner');
      component.toggleForm();
      webhookService.createWebhook.mockRejectedValueOnce(new Error('boom'));
      component.form.patchValue({ url: 'https://a.example/hook' });
      fillEvents(['run.created']);

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(true);
      expect(fixture.nativeElement.textContent).toContain('adminWebhooks.createError');
    });
  });

  describe('row actions', () => {
    it('flips isActive and nothing else when toggling', async () => {
      setup('owner');
      const active = webhook({ id: 'w1', isActive: true });

      await component.toggleActive(active);

      expect(webhookService.updateWebhook).toHaveBeenCalledWith('w1', { isActive: false });
      expect(component.busyId()).toBeNull();
    });

    it('deletes by id and clears the busy flag', async () => {
      setup('owner');

      await component.remove('w1');

      expect(webhookService.deleteWebhook).toHaveBeenCalledWith('w1');
      expect(component.busyId()).toBeNull();
    });

    it('clears the busy flag even when the delete fails', async () => {
      setup('owner');
      webhookService.deleteWebhook.mockRejectedValueOnce(new Error('boom'));

      await component.remove('w1');

      expect(component.busyId()).toBeNull();
    });
  });
});
