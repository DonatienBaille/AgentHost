import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { AuditLogComponent } from './audit-log.component';
import { AuditService } from '../../../services/audit.service';
import { AuthService } from '../../../services/auth.service';
import { AuditLogEntry, UserRole } from '../../../core/models';
import { auditEntry, user } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class AuditServiceStub {
  readonly entries = signal<AuditLogEntry[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listAuditLog = vi.fn(async (_orgId: string, _skip?: number, _take?: number) => {});
}

describe('AuditLogComponent', () => {
  let fixture: ComponentFixture<AuditLogComponent>;
  let auditService: AuditServiceStub;
  let authService: AuthService;

  function setup(role: UserRole | null = 'owner', orgId: string | null = 'o1'): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [AuditLogComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: AuditService, useClass: AuditServiceStub },
      ],
    });

    auditService = TestBed.inject(AuditService) as unknown as AuditServiceStub;
    authService = TestBed.inject(AuthService);
    // `orgId` non nul mais vide : reproduit une session dont l'organisation manque.
    authService.currentUser.set(role ? user(role, { orgId: orgId ?? '' }) : null);

    fixture = TestBed.createComponent(AuditLogComponent);
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function rows(): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll('[data-testid="audit-row"]');
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('list states', () => {
    it('loads the audit log of the signed-in user org', () => {
      setup('owner', 'org-42');

      expect(auditService.listAuditLog).toHaveBeenCalledTimes(1);
      expect(auditService.listAuditLog).toHaveBeenCalledWith('org-42');
    });

    it('loads nothing when there is no session', () => {
      setup(null);

      expect(auditService.listAuditLog).not.toHaveBeenCalled();
    });

    it('loads nothing when the session carries no org id', () => {
      setup('owner', null);

      expect(auditService.listAuditLog).not.toHaveBeenCalled();
    });

    it('shows the empty state and no rows when the log comes back empty', () => {
      setup();

      expect(rows().length).toBe(0);
      expect(el('audit-empty')).not.toBeNull();
    });

    it('hides the empty state while loading', () => {
      setup();
      auditService.isLoading.set(true);
      fixture.detectChanges();

      expect(el('audit-empty')).toBeNull();
    });

    it('renders the service error message', () => {
      setup();
      auditService.error.set('errors.loadAuditLog');
      fixture.detectChanges();

      expect(el('audit-error')!.textContent).toContain('errors.loadAuditLog');
    });

    it('renders one row per entry with action, actor and resource', () => {
      setup();
      auditService.entries.set([
        auditEntry({
          id: 'a1',
          action: 'secret.created',
          actorUserId: 'u9',
          resourceType: 'secret',
          resourceId: 's1',
        }),
        auditEntry({
          id: 'a2',
          action: 'run.created',
          actorUserId: null,
          resourceType: null,
          resourceId: null,
        }),
      ]);
      fixture.detectChanges();

      expect(rows().length).toBe(2);
      expect(el('audit-empty')).toBeNull();

      const first = rows()[0].querySelectorAll('td');
      expect(first[0].textContent!.trim()).toBe('secret.created');
      expect(first[1].textContent!.trim()).toBe('u9');
      expect(first[2].textContent!.trim()).toBe('secret:s1');

      // Acteur et ressource absents (action systeme) : tiret cadratin, jamais "null".
      const second = rows()[1].querySelectorAll('td');
      expect(second[1].textContent!.trim()).toBe('—');
      expect(second[2].textContent!.trim()).toBe('—');
    });

    it('never renders the raw changes or details payloads', () => {
      setup();
      auditService.entries.set([
        auditEntry({ changes: { password: 'leaked' }, details: { ip: '10.0.0.1' } }),
      ]);
      fixture.detectChanges();

      // Le tableau ne projette que quatre colonnes ; le contenu brut reste hors du DOM.
      expect(rows()[0].querySelectorAll('td').length).toBe(4);
      expect(fixture.nativeElement.textContent).not.toContain('leaked');
      expect(fixture.nativeElement.textContent).not.toContain('10.0.0.1');
    });
  });

  describe('role gating', () => {
    /**
     * Le serveur exige `maintainer` sur GET /api/organizations/{orgId}/audit-log. La page était
     * rendue à l'identique aux quatre rôles : un developer ou un viewer déclenchait un appel voué
     * au 403, affiché comme une erreur de chargement générique — « ça n'a pas marché » là où la
     * vraie réponse est « ce n'est pas pour vous ».
     */
    it.each<UserRole>(['owner', 'maintainer'])('renders the log to a %s', (role) => {
      setup(role);
      auditService.entries.set([auditEntry({ action: 'user.role_changed', actorUserId: 'u9' })]);
      fixture.detectChanges();

      expect(auditService.listAuditLog).toHaveBeenCalledWith('o1');
      expect(rows().length).toBe(1);
      expect(rows()[0].textContent).toContain('user.role_changed');
      expect(el('audit-forbidden')).toBeNull();
    });

    it.each<UserRole>(['developer', 'viewer'])(
      'tells a %s the log is not theirs, and never asks the server for it',
      (role) => {
        setup(role);
        auditService.entries.set([auditEntry({ action: 'user.role_changed' })]);
        fixture.detectChanges();

        // Ne pas demander ce qu'on n'a pas le droit de lire : le 403 n'apprend rien à personne.
        expect(auditService.listAuditLog).not.toHaveBeenCalled();
        expect(el('audit-forbidden')).not.toBeNull();
        expect(rows().length).toBe(0);
      },
    );

    it('offers no action at all: the page is read-only for every role', () => {
      setup('owner');
      auditService.entries.set([auditEntry()]);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelectorAll('button').length).toBe(0);
      expect(fixture.nativeElement.querySelectorAll('form, input, select').length).toBe(0);
    });
  });
});
