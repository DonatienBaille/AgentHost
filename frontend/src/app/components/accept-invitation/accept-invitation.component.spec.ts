import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { AcceptInvitationComponent } from './accept-invitation.component';
import { AuthService } from '../../services/auth.service';
import { user } from '../../core/testing/fixtures';

/**
 * L'écran d'acceptation d'invitation, cible du lien envoyé par courriel.
 *
 * Deux propriétés méritent d'être épinglées. D'abord, sans jeton l'écran ne propose **aucun**
 * formulaire : offrir un champ de mot de passe laisserait croire qu'un compte va être créé.
 * Ensuite, contrairement à la réinitialisation, l'acceptation **ouvre la session** — c'est une
 * différence délibérée, et un test la fige pour qu'on ne l'aligne pas par mégarde sur l'autre écran.
 */
class AuthServiceStub {
  acceptInvitation = vi.fn(async (_token: string, _password: string, _displayName?: string) =>
    user('developer'),
  );
}

describe('AcceptInvitationComponent', () => {
  let fixture: ComponentFixture<AcceptInvitationComponent>;
  let component: AcceptInvitationComponent;
  let auth: AuthServiceStub;
  let navigateByUrl: ReturnType<typeof vi.spyOn>;

  function setup(token: string | null): void {
    auth = new AuthServiceStub();
    TestBed.configureTestingModule({
      imports: [AcceptInvitationComponent],
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

    fixture = TestBed.createComponent(AcceptInvitationComponent);
    component = fixture.componentInstance;
    navigateByUrl = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('lien incomplet', () => {
    it('offers no form at all when the URL carries no token', () => {
      setup(null);

      expect(el('invitation-missing-token')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('#invite-password')).toBeNull();
      expect(el('invitation-submit')).toBeNull();
    });

    it('treats a blank token as no token at all', () => {
      setup('  ');
      expect(component.token()).toBeNull();
      expect(el('invitation-missing-token')).not.toBeNull();
    });

    it('does not call the service even if submit is forced', async () => {
      setup(null);
      component.form.setValue({ password: 'a-brand-new-passphrase', displayName: '' });

      await component.submit();

      expect(auth.acceptInvitation).not.toHaveBeenCalled();
    });
  });

  describe('acceptation', () => {
    it('shows the form when the URL carries a token', () => {
      setup('inv-123');

      expect(component.token()).toBe('inv-123');
      expect(fixture.nativeElement.querySelector('#invite-password')).not.toBeNull();
      expect(el('invitation-missing-token')).toBeNull();
    });

    it('sends the token, the chosen password and the display name', async () => {
      setup('inv-123');
      component.form.setValue({ password: 'a-brand-new-passphrase', displayName: 'Camille' });

      await component.submit();

      expect(auth.acceptInvitation).toHaveBeenCalledExactlyOnceWith(
        'inv-123',
        'a-brand-new-passphrase',
        'Camille',
      );
    });

    it('omits an empty display name rather than sending a blank string', async () => {
      setup('inv-123');
      component.form.setValue({ password: 'a-brand-new-passphrase', displayName: '' });

      await component.submit();

      expect(auth.acceptInvitation).toHaveBeenCalledExactlyOnceWith(
        'inv-123',
        'a-brand-new-passphrase',
        undefined,
      );
    });

    it('refuses a password shorter than the policy without asking the server', async () => {
      setup('inv-123');
      component.form.setValue({ password: 'short', displayName: '' });

      await component.submit();

      expect(auth.acceptInvitation).not.toHaveBeenCalled();
    });

    /**
     * La différence délibérée avec la réinitialisation : ici on entre dans l'application.
     */
    it('lands the invitee inside the application, session opened', async () => {
      setup('inv-123');
      component.form.setValue({ password: 'a-brand-new-passphrase', displayName: '' });

      await component.submit();

      expect(navigateByUrl).toHaveBeenCalledWith('/');
    });

    it('reports a dead invitation and stays put', async () => {
      setup('inv-dead');
      auth.acceptInvitation.mockRejectedValueOnce(new Error('410'));
      component.form.setValue({ password: 'a-brand-new-passphrase', displayName: '' });

      await component.submit();
      fixture.detectChanges();

      expect(el('invitation-error')).not.toBeNull();
      expect(navigateByUrl).not.toHaveBeenCalled();
      // Le formulaire reste disponible : l'invité peut corriger son mot de passe et réessayer.
      expect(el('invitation-submit')).not.toBeNull();
    });
  });
});
