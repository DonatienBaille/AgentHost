import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { ResetPasswordComponent } from './reset-password.component';
import { AuthService } from '../../services/auth.service';

/**
 * L'écran de réinitialisation, cible du lien envoyé par courriel.
 *
 * Le test qui compte est celui de la **non-divulgation** : le serveur répond 202 que le compte
 * existe ou non, précisément pour ne pas révéler quelles adresses sont enregistrées. Un écran qui
 * distinguerait les deux cas — message différent, erreur affichée, formulaire qui reste ouvert —
 * défairait cette propriété côté client, là où elle est le plus visible.
 */
class AuthServiceStub {
  requestPasswordReset = vi.fn(async (_email: string) => {});
  confirmPasswordReset = vi.fn(async (_token: string, _password: string) => {});
}

describe('ResetPasswordComponent', () => {
  let fixture: ComponentFixture<ResetPasswordComponent>;
  let component: ResetPasswordComponent;
  let auth: AuthServiceStub;

  function setup(token: string | null): void {
    auth = new AuthServiceStub();
    TestBed.configureTestingModule({
      imports: [ResetPasswordComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: AuthService, useValue: auth },
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: new Map([['token', token]]) } },
        },
      ],
    });

    // Map n'expose pas `get` avec la sémantique de ParamMap pour une clé absente ; une Map
    // renvoie undefined là où ParamMap renvoie null. Le composant traite les deux pareil.
    fixture = TestBed.createComponent(ResetPasswordComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('demande de lien (sans jeton)', () => {
    it('shows the request form when the URL carries no token', () => {
      setup(null);

      expect(component.token()).toBeNull();
      expect(fixture.nativeElement.querySelector('#reset-email')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('#reset-new-password')).toBeNull();
    });

    it('treats a blank token as no token at all', () => {
      setup('   ');
      expect(component.token()).toBeNull();
    });

    it('sends the address to the service', async () => {
      setup(null);
      component.requestForm.setValue({ email: 'someone@example.com' });

      await component.submitRequest();

      expect(auth.requestPasswordReset).toHaveBeenCalledExactlyOnceWith('someone@example.com');
    });

    it('refuses to send a malformed address rather than asking the server', async () => {
      setup(null);
      component.requestForm.setValue({ email: 'not-an-email' });

      await component.submitRequest();

      expect(auth.requestPasswordReset).not.toHaveBeenCalled();
      expect(component.requestSent()).toBe(false);
    });

    /**
     * Le cœur du fichier. Le serveur ne dit pas si le compte existe ; l'écran non plus.
     */
    it('shows the same acknowledgement whether the call succeeds or fails', async () => {
      setup(null);
      component.requestForm.setValue({ email: 'known@example.com' });
      await component.submitRequest();
      fixture.detectChanges();
      const afterSuccess = el('reset-request-sent')?.textContent?.trim();

      // Deux montages dans un même test : il faut rendre le TestBed avant de le reconfigurer.
      TestBed.resetTestingModule();
      setup(null);
      auth.requestPasswordReset.mockRejectedValueOnce(new Error('boom'));
      component.requestForm.setValue({ email: 'unknown@example.com' });
      await component.submitRequest();
      fixture.detectChanges();
      const afterFailure = el('reset-request-sent')?.textContent?.trim();

      expect(afterSuccess).toBeTruthy();
      expect(afterFailure).toBe(afterSuccess);
      // Et surtout : aucune bannière d'erreur, qui trahirait la différence.
      expect(el('reset-error')).toBeNull();
    });

    it('replaces the form with the acknowledgement, so the address cannot be probed twice', async () => {
      setup(null);
      component.requestForm.setValue({ email: 'someone@example.com' });

      await component.submitRequest();
      fixture.detectChanges();

      expect(el('reset-request-sent')).not.toBeNull();
      expect(el('reset-request-submit')).toBeNull();
    });
  });

  describe('choix du nouveau mot de passe (avec jeton)', () => {
    it('shows the password form when the URL carries a token', () => {
      setup('tok-123');

      expect(component.token()).toBe('tok-123');
      expect(fixture.nativeElement.querySelector('#reset-new-password')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('#reset-email')).toBeNull();
    });

    it('sends the token from the URL together with the chosen password', async () => {
      setup('tok-123');
      component.resetForm.setValue({ password: 'a-brand-new-passphrase' });

      await component.submitReset();

      expect(auth.confirmPasswordReset).toHaveBeenCalledExactlyOnceWith(
        'tok-123',
        'a-brand-new-passphrase',
      );
    });

    it('refuses a password shorter than the policy without asking the server', async () => {
      setup('tok-123');
      component.resetForm.setValue({ password: 'short' });

      await component.submitReset();

      expect(auth.confirmPasswordReset).not.toHaveBeenCalled();
    });

    it('confirms success and offers to sign in — without opening a session itself', async () => {
      setup('tok-123');
      component.resetForm.setValue({ password: 'a-brand-new-passphrase' });

      await component.submitReset();
      fixture.detectChanges();

      expect(el('reset-done')).not.toBeNull();
      expect(el('reset-go-login')).not.toBeNull();
      // Le serveur ne renvoie pas de jetons ici : l'écran ne doit pas prétendre le contraire.
      expect(component.resetDone()).toBe(true);
    });

    it('reports a dead link instead of pretending the password changed', async () => {
      setup('tok-dead');
      auth.confirmPasswordReset.mockRejectedValueOnce(new Error('410'));
      component.resetForm.setValue({ password: 'a-brand-new-passphrase' });

      await component.submitReset();
      fixture.detectChanges();

      expect(el('reset-error')).not.toBeNull();
      expect(component.resetDone()).toBe(false);
      expect(el('reset-done')).toBeNull();
    });

    it('navigates to the login screen when asked', async () => {
      setup('tok-123');
      const router = TestBed.inject(Router);
      const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

      component.goToLogin();

      expect(navigate).toHaveBeenCalledWith(['/login']);
    });
  });
});
