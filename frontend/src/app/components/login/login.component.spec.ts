import { ComponentFixture, TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { LoginComponent } from './login.component';
import { AuthService } from '../../services/auth.service';
import { User } from '../../core/models';
import { user } from '../../core/testing/fixtures';

/** Double du service d'auth : aucune requête, aucun stockage local. */
class AuthServiceStub {
  readonly login = vi.fn(async (_email: string, _password: string): Promise<User> => user());
  readonly register = vi.fn(
    async (
      _orgName: string,
      _orgSlug: string,
      _email: string,
      _password: string,
      _displayName?: string,
    ): Promise<User> => user(),
  );
}

describe('LoginComponent', () => {
  let fixture: ComponentFixture<LoginComponent>;
  let component: LoginComponent;
  let authService: AuthServiceStub;
  let navigateByUrl: ReturnType<typeof vi.spyOn>;

  /** `queryParams` alimente le `returnUrl` lu au chargement via `route.snapshot`. */
  function setup(queryParams: Record<string, string> = {}): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [LoginComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: AuthService, useClass: AuthServiceStub },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } },
        },
      ],
    });

    authService = TestBed.inject(AuthService) as unknown as AuthServiceStub;
    const router = TestBed.inject(Router);
    navigateByUrl = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fixture = TestBed.createComponent(LoginComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el<T extends HTMLElement>(selector: string): T | null {
    return fixture.nativeElement.querySelector(selector);
  }

  function fillLogin(email = 'someone@example.com', password = 'hunter2hunter2'): void {
    component.loginForm.setValue({ email, password });
  }

  function fillRegister(overrides: Partial<Record<string, string>> = {}): void {
    component.registerForm.setValue({
      orgName: 'Acme',
      orgSlug: 'acme',
      email: 'someone@example.com',
      password: 'hunter2hunter2',
      displayName: '',
      ...overrides,
    } as never);
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('mode switching', () => {
    it('starts in sign-in mode and renders only the login form', () => {
      setup();

      expect(component.mode()).toBe('login');
      expect(el('#login-email')).not.toBeNull();
      expect(el('#reg-org-name')).toBeNull();
    });

    it('swaps to the register form, with the org fields, when switching mode', () => {
      setup();

      component.switchMode('register');
      fixture.detectChanges();

      expect(el('#login-email')).toBeNull();
      expect(el('#reg-org-name')).not.toBeNull();
      expect(el('#reg-org-slug')).not.toBeNull();
      expect(el('#reg-display-name')).not.toBeNull();
    });

    it('clears a pending error when the mode changes', async () => {
      setup();
      authService.login.mockRejectedValueOnce(new Error('boom'));
      fillLogin();
      await component.submitLogin();
      expect(component.errorKey()).toBe('login.genericError');

      component.switchMode('register');

      expect(component.errorKey()).toBeNull();
    });

    it('renders the error banner from the translation key', async () => {
      setup();
      authService.login.mockRejectedValueOnce(new Error('boom'));
      fillLogin();

      await component.submitLogin();
      fixture.detectChanges();

      // Clés non résolues en test : ngx-translate rend la clé telle quelle.
      expect(fixture.nativeElement.textContent).toContain('login.genericError');
    });
  });

  describe('org slug auto-fill', () => {
    it('slugifies the org name into the slug field', () => {
      setup();
      component.switchMode('register');

      component.onOrgNameInput('Acme Corp');

      expect(component.registerForm.getRawValue().orgSlug).toBe('acme-corp');
    });

    it('strips accents-free punctuation, collapses runs and trims dashes', () => {
      setup();

      component.onOrgNameInput('  Hello,   World!!  ');

      expect(component.registerForm.getRawValue().orgSlug).toBe('hello-world');
    });

    it('produces an empty slug when the name has no slug-able characters', () => {
      setup();

      component.onOrgNameInput('***');

      expect(component.registerForm.getRawValue().orgSlug).toBe('');
    });

    it('stops overwriting the slug once the user has edited it by hand', () => {
      setup();
      component.onOrgNameInput('Acme');
      expect(component.registerForm.getRawValue().orgSlug).toBe('acme');

      component.onOrgSlugInput();
      component.registerForm.patchValue({ orgSlug: 'my-own-slug' });
      component.onOrgNameInput('Something Else Entirely');

      expect(component.registerForm.getRawValue().orgSlug).toBe('my-own-slug');
    });

    it('keeps the manual flag across a mode round-trip', () => {
      // Comportement ACTUEL épinglé : `switchMode` ne réarme pas l'auto-slugification.
      setup();
      component.onOrgSlugInput();
      component.registerForm.patchValue({ orgSlug: 'kept' });

      component.switchMode('login');
      component.switchMode('register');
      component.onOrgNameInput('Brand New Name');

      expect(component.registerForm.getRawValue().orgSlug).toBe('kept');
    });

    it('auto-fills the slug from a real input event on the name field', () => {
      setup();
      component.switchMode('register');
      fixture.detectChanges();

      const name = el<HTMLInputElement>('#reg-org-name')!;
      name.value = 'Agent Host';
      name.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      expect(el<HTMLInputElement>('#reg-org-slug')!.value).toBe('agent-host');
    });
  });

  describe('login validation and payload', () => {
    it('starts invalid and refuses to submit an empty form', async () => {
      setup();

      await component.submitLogin();

      expect(authService.login).not.toHaveBeenCalled();
      expect(component.loginForm.touched).toBe(true);
    });

    it('rejects a malformed email', async () => {
      setup();
      component.loginForm.setValue({ email: 'not-an-email', password: 'pw' });

      await component.submitLogin();

      expect(component.loginForm.controls.email.hasError('email')).toBe(true);
      expect(authService.login).not.toHaveBeenCalled();
    });

    it('sends exactly the email and password, in that order', async () => {
      setup();
      fillLogin('me@example.com', 's3cret!!');

      await component.submitLogin();

      expect(authService.login).toHaveBeenCalledTimes(1);
      expect(authService.login).toHaveBeenCalledWith('me@example.com', 's3cret!!');
    });

    it('clears the submitting flag and navigates home on success', async () => {
      setup();
      fillLogin();

      await component.submitLogin();

      expect(component.isSubmitting()).toBe(false);
      expect(component.errorKey()).toBeNull();
      expect(navigateByUrl).toHaveBeenCalledWith('/');
    });

    it('maps a 401 to the invalid-credentials key and does not navigate', async () => {
      setup();
      authService.login.mockRejectedValueOnce(
        new HttpErrorResponse({ status: 401, statusText: 'Unauthorized' }),
      );
      fillLogin();

      await component.submitLogin();

      expect(component.errorKey()).toBe('login.invalidCredentials');
      expect(navigateByUrl).not.toHaveBeenCalled();
      expect(component.isSubmitting()).toBe(false);
    });

    it('maps any other HTTP failure to the generic key', async () => {
      setup();
      authService.login.mockRejectedValueOnce(
        new HttpErrorResponse({ status: 500, statusText: 'Server Error' }),
      );
      fillLogin();

      await component.submitLogin();

      expect(component.errorKey()).toBe('login.genericError');
    });

    it('disables the submit button while the request is in flight', async () => {
      setup();
      let release!: (u: User) => void;
      authService.login.mockReturnValueOnce(
        new Promise<User>((resolve) => {
          release = resolve;
        }),
      );
      fillLogin();

      const pending = component.submitLogin();
      fixture.detectChanges();
      expect(component.isSubmitting()).toBe(true);
      expect(el<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(true);

      release(user());
      await pending;
      fixture.detectChanges();
      expect(el<HTMLButtonElement>('button[type="submit"]')!.disabled).toBe(false);
    });
  });

  describe('register validation and payload', () => {
    it('refuses a slug that is not lower-kebab', async () => {
      setup();
      component.switchMode('register');
      fillRegister({ orgSlug: 'Acme Corp' });

      await component.submitRegister();

      expect(component.registerForm.controls.orgSlug.hasError('pattern')).toBe(true);
      expect(authService.register).not.toHaveBeenCalled();
    });

    it('refuses a password shorter than 8 characters', async () => {
      setup();
      fillRegister({ password: 'short' });

      await component.submitRegister();

      expect(component.registerForm.controls.password.hasError('minlength')).toBe(true);
      expect(authService.register).not.toHaveBeenCalled();
    });

    it('sends the five positional arguments in contract order', async () => {
      setup();
      fillRegister({ displayName: 'Donatien' });

      await component.submitRegister();

      expect(authService.register).toHaveBeenCalledWith(
        'Acme',
        'acme',
        'someone@example.com',
        'hunter2hunter2',
        'Donatien',
      );
    });

    it('sends undefined rather than an empty display name', async () => {
      setup();
      fillRegister({ displayName: '' });

      await component.submitRegister();

      expect(authService.register.mock.calls[0][4]).toBeUndefined();
    });

    it('maps a 409 to the taken-slug key', async () => {
      setup();
      authService.register.mockRejectedValueOnce(
        new HttpErrorResponse({ status: 409, statusText: 'Conflict' }),
      );
      fillRegister();

      await component.submitRegister();

      expect(component.errorKey()).toBe('login.orgSlugTaken');
      expect(navigateByUrl).not.toHaveBeenCalled();
    });

    it('maps a non-HTTP failure to the generic key and stops submitting', async () => {
      setup();
      authService.register.mockRejectedValueOnce(new Error('offline'));
      fillRegister();

      await component.submitRegister();

      expect(component.errorKey()).toBe('login.genericError');
      expect(component.isSubmitting()).toBe(false);
    });

    it('navigates after a successful registration', async () => {
      setup({ returnUrl: '/projects/p1' });
      fillRegister();

      await component.submitRegister();

      expect(navigateByUrl).toHaveBeenCalledWith('/projects/p1');
    });
  });

  describe('returnUrl sanitisation', () => {
    it('honours a same-origin absolute path, query string included', async () => {
      setup({ returnUrl: '/projects/p1/runs?status=running' });
      fillLogin();

      await component.submitLogin();

      expect(navigateByUrl).toHaveBeenCalledWith('/projects/p1/runs?status=running');
    });

    it.each([
      ['an absolute https URL', 'https://evil.example'],
      ['an absolute http URL', 'http://evil.example/steal'],
      ['a protocol-relative URL', '//evil.example'],
      ['a protocol-relative URL with a path', '//evil.example/pwn'],
      ['a javascript: URL', 'javascript:alert(1)'],
      ['a data: URL', 'data:text/html,<script>alert(1)</script>'],
      ['a scheme-relative host without a leading slash', 'evil.example'],
      ['an empty value', ''],
    ])('refuses to open-redirect on %s', async (_label, returnUrl) => {
      setup({ returnUrl });
      fillLogin();

      await component.submitLogin();

      expect(navigateByUrl).toHaveBeenCalledTimes(1);
      expect(navigateByUrl).toHaveBeenCalledWith('/');
    });

    it('falls back home when no returnUrl is present at all', async () => {
      setup();
      fillLogin();

      await component.submitLogin();

      expect(navigateByUrl).toHaveBeenCalledWith('/');
    });

    it('lets a backslash-prefixed path through — pinned current behaviour', async () => {
      // DÉFAUT ÉPINGLÉ (login.component.ts:119) : `sanitizeReturnUrl` ne rejette que `//`.
      // `/\evil.example` passe le filtre alors que plusieurs navigateurs normalisent `\` en `/`,
      // ce qui en ferait une URL protocol-relative. Angular route en interne ici, donc l'impact
      // reste théorique, mais le garde-fou est plus étroit que ce que son commentaire promet.
      setup({ returnUrl: '/\\evil.example' });
      fillLogin();

      await component.submitLogin();

      expect(navigateByUrl).toHaveBeenCalledWith('/\\evil.example');
    });

    it('re-reads nothing after construction: the URL is taken from the snapshot at submit time', async () => {
      setup({ returnUrl: '/dashboard' });
      fillLogin();

      await component.submitLogin();
      await component.submitLogin();

      expect(navigateByUrl).toHaveBeenNthCalledWith(1, '/dashboard');
      expect(navigateByUrl).toHaveBeenNthCalledWith(2, '/dashboard');
    });
  });
});
