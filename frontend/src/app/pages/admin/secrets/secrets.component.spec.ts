import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { SecretsComponent } from './secrets.component';
import { SecretService } from '../../../services/secret.service';
import { AuthService } from '../../../services/auth.service';
import { CreateSecretRequest, Secret, UserRole } from '../../../core/models';
import { secret, user } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class SecretServiceStub {
  readonly secrets = signal<Secret[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listSecrets = vi.fn(async () => {});
  readonly createSecret = vi.fn(async (req: CreateSecretRequest) => secret({ name: req.name }));
  readonly rotateSecret = vi.fn(async (id: string, _value: string) => secret({ id }));
  readonly deleteSecret = vi.fn(async (_id: string) => {});
}

describe('SecretsComponent', () => {
  let fixture: ComponentFixture<SecretsComponent>;
  let component: SecretsComponent;
  let secretService: SecretServiceStub;
  let authService: AuthService;

  function setup(role: UserRole | null = 'maintainer'): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [SecretsComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: SecretService, useClass: SecretServiceStub },
      ],
    });

    secretService = TestBed.inject(SecretService) as unknown as SecretServiceStub;
    authService = TestBed.inject(AuthService);
    authService.currentUser.set(role ? user(role) : null);

    fixture = TestBed.createComponent(SecretsComponent);
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
    it('shows the empty state and no rows when the list comes back empty', () => {
      setup('maintainer');

      expect(all('secret-row').length).toBe(0);
      expect(el('secrets-empty')).not.toBeNull();
      // Pas de tableau vide derrière l'état vide.
      expect(fixture.nativeElement.querySelector('table')).toBeNull();
    });

    it('shows the error state instead of the table, with a retry that reloads', () => {
      setup('maintainer');
      secretService.error.set('adminSecrets.loadError');
      fixture.detectChanges();

      const banner = el('secrets-error');
      expect(banner).not.toBeNull();
      expect(banner!.textContent).toContain('adminSecrets.loadError');
      expect(el('secrets-empty')).toBeNull();
      expect(fixture.nativeElement.querySelector('table')).toBeNull();

      secretService.listSecrets.mockClear();
      el('secrets-retry')!.click();
      expect(secretService.listSecrets).toHaveBeenCalledTimes(1);
    });

    it('shows the spinner instead of the error while loading', () => {
      setup('maintainer');
      secretService.isLoading.set(true);
      secretService.error.set('adminSecrets.loadError');
      fixture.detectChanges();

      expect(el('secrets-error')).toBeNull();
      expect(fixture.nativeElement.querySelector('.animate-spin')).not.toBeNull();
    });

    it('renders one row per secret with the displayed fields', () => {
      setup('maintainer');
      secretService.secrets.set([
        secret({ id: 's1', name: 'OPENAI_API_KEY', scope: 'org', lastUsedAt: null }),
        secret({
          id: 's2',
          name: 'DEPLOY_TOKEN',
          scope: 'project',
          projectId: 'p1',
          lastUsedAt: '2026-02-02T00:00:00Z',
        }),
      ]);
      fixture.detectChanges();

      const rows = all('secret-row');
      expect(rows.length).toBe(2);
      expect(el('secrets-empty')).toBeNull();

      const first = rows[0].querySelectorAll('td');
      expect(first[0].textContent!.trim()).toBe('OPENAI_API_KEY');
      // Clés non résolues en test : ngx-translate rend la clé telle quelle.
      expect(first[1].textContent!.trim()).toBe('adminSecrets.scopeOrg');
      expect(first[2].textContent!.trim()).toBe('—');

      const second = rows[1].querySelectorAll('td');
      expect(second[1].textContent!.trim()).toBe('adminSecrets.scopeProject');
      expect(second[2].textContent!.trim()).not.toBe('—');
    });

    it('never renders a secret value: the model carries none', () => {
      setup('maintainer');
      secretService.secrets.set([secret({ name: 'OPENAI_API_KEY' })]);
      fixture.detectChanges();

      expect(Object.keys(secret())).not.toContain('value');
      const row = all('secret-row')[0];
      expect(row.querySelectorAll('input').length).toBe(0);
    });
  });

  describe('role gating', () => {
    it('lets a maintainer manage secrets', () => {
      setup('maintainer');

      expect(el('secrets-forbidden')).toBeNull();
      expect(el('new-secret')).not.toBeNull();
      expect(secretService.listSecrets).toHaveBeenCalledTimes(1);
    });

    it('lets an owner manage secrets', () => {
      setup('owner');

      expect(el('secrets-forbidden')).toBeNull();
      expect(el('new-secret')).not.toBeNull();
    });

    it.each<UserRole>(['developer', 'viewer'])(
      'shows a %s the forbidden panel and no action at all',
      (role) => {
        setup(role);

        expect(el('secrets-forbidden')).not.toBeNull();
        expect(el('new-secret')).toBeNull();
        expect(el('secrets-empty')).toBeNull();
        expect(all('rotate-secret').length).toBe(0);
        expect(all('delete-secret').length).toBe(0);
        // La page n'appelle même pas la route maintainer+ : pas de 403 inutile.
        expect(secretService.listSecrets).not.toHaveBeenCalled();
      },
    );

    it('hides the rows from a developer even if the service already holds secrets', () => {
      setup('developer');
      secretService.secrets.set([secret()]);
      fixture.detectChanges();

      expect(all('secret-row').length).toBe(0);
      expect(el('secrets-forbidden')).not.toBeNull();
    });

    it('treats a signed-out user as unable to manage', () => {
      setup(null);

      expect(el('secrets-forbidden')).not.toBeNull();
      expect(el('new-secret')).toBeNull();
    });

    it('offers rotate and delete on every row for a maintainer', () => {
      setup('maintainer');
      secretService.secrets.set([secret({ id: 's1' }), secret({ id: 's2' })]);
      fixture.detectChanges();

      expect(all('rotate-secret').length).toBe(2);
      expect(all('delete-secret').length).toBe(2);
    });
  });

  describe('create payload', () => {
    it('sends an org-scoped secret without a projectId', async () => {
      setup('maintainer');
      component.form.setValue({
        name: 'OPENAI_API_KEY',
        value: 'sk-secret',
        scope: 'org',
        projectId: '',
      });

      await component.submit();

      expect(secretService.createSecret).toHaveBeenCalledWith({
        name: 'OPENAI_API_KEY',
        value: 'sk-secret',
        scope: 'org',
        projectId: undefined,
      });
    });

    it('drops a leftover projectId when the scope is org', async () => {
      setup('maintainer');
      component.form.setValue({
        name: 'OPENAI_API_KEY',
        value: 'sk-secret',
        scope: 'org',
        projectId: 'p1',
      });

      await component.submit();

      expect(secretService.createSecret).toHaveBeenCalledWith(
        expect.objectContaining({ scope: 'org', projectId: undefined }),
      );
    });

    it('sends the projectId when the scope is project', async () => {
      setup('maintainer');
      component.form.setValue({
        name: 'DEPLOY_TOKEN',
        value: 'tok',
        scope: 'project',
        projectId: 'p1',
      });

      await component.submit();

      expect(secretService.createSecret).toHaveBeenCalledWith({
        name: 'DEPLOY_TOKEN',
        value: 'tok',
        scope: 'project',
        projectId: 'p1',
      });
      // Aucun orgId dans le corps : le serveur le déduit du JWT (CreateSecretRequest).
      const sent = secretService.createSecret.mock.calls[0][0] as unknown as Record<
        string,
        unknown
      >;
      expect(Object.keys(sent).sort()).toEqual(['name', 'projectId', 'scope', 'value']);
    });

    it('does not call the service when the value is missing', async () => {
      setup('maintainer');
      component.form.setValue({ name: 'X', value: '', scope: 'org', projectId: '' });

      await component.submit();

      expect(secretService.createSecret).not.toHaveBeenCalled();
    });

    it('surfaces a create failure without closing the form', async () => {
      setup('maintainer');
      component.toggleForm();
      secretService.createSecret.mockRejectedValueOnce(new Error('boom'));
      component.form.setValue({ name: 'X', value: 'v', scope: 'org', projectId: '' });

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(true);
      expect(fixture.nativeElement.textContent).toContain('adminSecrets.createError');
    });
  });

  describe('rotate and delete', () => {
    beforeEach(() => {
      setup('maintainer');
      secretService.secrets.set([secret({ id: 's1' }), secret({ id: 's2', name: 'OTHER' })]);
      fixture.detectChanges();
    });

    it('opens the inline rotate row only for the clicked secret', () => {
      all('rotate-secret')[0].click();
      fixture.detectChanges();

      const rotateRows = all('rotate-row');
      expect(rotateRows.length).toBe(1);
      expect(rotateRows[0].querySelector('input')!.id).toBe('rotate-s1');
    });

    it('sends only the new value, keyed by secret id', async () => {
      component.startRotate('s1');
      component.onRotateValueInput('brand-new-value');

      await component.confirmRotate();

      expect(secretService.rotateSecret).toHaveBeenCalledTimes(1);
      expect(secretService.rotateSecret).toHaveBeenCalledWith('s1', 'brand-new-value');
      expect(component.rotatingId()).toBeNull();
      expect(component.rotatedId()).toBe('s1');
      // La valeur saisie ne survit pas à la rotation.
      expect(component.rotateValue()).toBe('');
    });

    it('does nothing when the new value is empty', async () => {
      component.startRotate('s1');

      await component.confirmRotate();

      expect(secretService.rotateSecret).not.toHaveBeenCalled();
    });

    it('keeps the rotate row open and shows the error when rotation fails', async () => {
      secretService.rotateSecret.mockRejectedValueOnce(new Error('boom'));
      component.startRotate('s1');
      component.onRotateValueInput('v');

      await component.confirmRotate();
      fixture.detectChanges();

      expect(component.rotatingId()).toBe('s1');
      expect(component.isRotating()).toBe(false);
      expect(el('rotate-row')!.textContent).toContain('adminSecrets.rotateError');
    });

    it('deletes by id and clears the busy flag', async () => {
      await component.remove('s2');

      expect(secretService.deleteSecret).toHaveBeenCalledWith('s2');
      expect(component.deletingId()).toBeNull();
    });

    it('clears the busy flag even when the delete fails', async () => {
      secretService.deleteSecret.mockRejectedValueOnce(new Error('boom'));

      await component.remove('s2');

      expect(component.deletingId()).toBeNull();
    });
  });
});
