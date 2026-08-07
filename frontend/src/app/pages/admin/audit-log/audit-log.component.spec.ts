import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { computed, signal } from '@angular/core';
import { AuditLogComponent } from './audit-log.component';
import { AuditService } from '../../../services/audit.service';
import { AuthService } from '../../../services/auth.service';
import { AuditEntryView, AuditFacets, AuditFilters, UserRole } from '../../../core/models';
import { auditEntryView, auditFacets, user } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class AuditServiceStub {
  readonly entries = signal<AuditEntryView[]>([]);
  readonly total = signal(0);
  readonly skip = signal(0);
  readonly take = signal(50);
  readonly facets = signal<AuditFacets | null>(null);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly hasMore = computed(() => this.skip() + this.entries().length < this.total());

  readonly search = vi.fn(async (_orgId: string, _filters?: AuditFilters, _skip?: number) => {});
  readonly loadFacets = vi.fn(async (_orgId: string) => {});
}

/**
 * La page du journal d'audit, devenue consultable (lot 3).
 *
 * Ce que ces tests épinglent, au-delà du rendu : la page ne demande jamais au serveur ce qu'elle
 * n'a pas le droit de lire ; la pagination repart de la première page dès qu'un filtre change ;
 * les charges JSONB n'entrent dans le DOM qu'une fois la ligne dépliée ; et « aucun résultat »
 * n'est pas « journal vide », parce que la première invite à élargir le filtre et la seconde dit
 * qu'il n'y a rien à chercher.
 */
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

  function all(testId: string): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`);
  }

  function rows(): NodeListOf<HTMLElement> {
    return all('audit-row');
  }

  function setEntries(entries: AuditEntryView[], total = entries.length): void {
    auditService.entries.set(entries);
    auditService.total.set(total);
    fixture.detectChanges();
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('loading', () => {
    it('loads the log and its facets for the signed-in user org', () => {
      setup('owner', 'org-42');

      expect(auditService.search).toHaveBeenCalledWith('org-42', {});
      // Les facettes peuplent les listes de filtres : sans elles, la barre n'offrirait rien.
      expect(auditService.loadFacets).toHaveBeenCalledWith('org-42');
    });

    it('loads nothing when there is no session', () => {
      setup(null);

      expect(auditService.search).not.toHaveBeenCalled();
      expect(auditService.loadFacets).not.toHaveBeenCalled();
    });

    it('loads nothing when the session carries no org id', () => {
      setup('owner', null);

      expect(auditService.search).not.toHaveBeenCalled();
    });

    it('renders the service error message', () => {
      setup();
      auditService.error.set('errors.loadAuditLog');
      fixture.detectChanges();

      expect(el('audit-error')!.textContent).toContain('errors.loadAuditLog');
    });
  });

  describe('rows', () => {
    it('renders one row per entry, with the actor named rather than identified', () => {
      setup();
      setEntries([
        auditEntryView({
          id: 'a1',
          action: 'secret.created',
          actorUserId: 'u9',
          actorDisplayName: 'Alice',
          actorEmail: 'alice@example.com',
          resourceType: 'secret',
          resourceId: 's1',
        }),
      ]);

      const cells = rows()[0].querySelectorAll('td');
      expect(cells[0].textContent!.trim()).toBe('secret.created');
      // Un ULID n'identifie personne : c'est tout l'intérêt de la jointure côté serveur.
      expect(cells[1].textContent!.trim()).toBe('Alice');
      expect(cells[2].textContent!.replace(/\s/g, '')).toBe('secret:s1');
    });

    it('falls back to the email, then to the id, when the name is missing', () => {
      setup();
      setEntries([
        auditEntryView({ id: 'a1', actorDisplayName: null, actorEmail: 'bob@example.com' }),
        auditEntryView({ id: 'a2', actorUserId: 'u7', actorDisplayName: null, actorEmail: null }),
      ]);

      expect(all('actor-link')[0].textContent!.trim()).toBe('bob@example.com');
      // Un compte que la jointure n'a pas résolu vaut encore mieux affiché que masqué.
      expect(all('actor-link')[1].textContent!.trim()).toBe('u7');
    });

    it('shows a system entry without inventing an actor', () => {
      setup();
      setEntries([auditEntryView({ actorUserId: null, actorEmail: null, actorDisplayName: null })]);

      expect(el('actor-system')!.textContent!.trim()).toBe('—');
      // Rien à filtrer : une entrée système n'a pas d'auteur à interroger.
      expect(el('actor-link')).toBeNull();
    });
  });

  describe('filters', () => {
    it('offers the actions the server reported, and nothing else', () => {
      setup();
      auditService.facets.set(auditFacets());
      fixture.detectChanges();

      const options = el('filter-action')!.querySelectorAll('option');
      // Le premier choix est « toutes » ; les suivants viennent des facettes, avec leur volume.
      expect(options.length).toBe(3);
      expect(options[1].textContent).toContain('run.created');
      expect(options[1].textContent).toContain('3');
    });

    it('sends only the filled criteria and restarts from the first page', async () => {
      setup();
      auditService.skip.set(100);
      fixture.detectChanges();

      fixture.componentInstance.filterForm.patchValue({ action: 'user.updated', from: '2031-03-03' });
      await fixture.componentInstance.applyFilters();

      // Page zéro : la page 3 d'un autre filtre n'existe pas, et l'y laisser afficherait un
      // « 101–150 sur 12 » incohérent.
      expect(auditService.search).toHaveBeenLastCalledWith(
        'o1',
        { action: 'user.updated', from: '2031-03-03' },
        0,
      );
    });

    it('clears every criterion on reset', async () => {
      setup();
      fixture.componentInstance.filterForm.patchValue({ action: 'user.updated', actorUserId: 'u9' });
      await fixture.componentInstance.applyFilters();

      await fixture.componentInstance.resetFilters();

      expect(auditService.search).toHaveBeenLastCalledWith('o1', {}, 0);
    });

    it('only offers the reset once a filter is actually applied', async () => {
      setup();
      expect(el('reset-filters')).toBeNull();

      fixture.componentInstance.filterForm.patchValue({ action: 'user.updated' });
      await fixture.componentInstance.applyFilters();
      fixture.detectChanges();

      expect(el('reset-filters')).not.toBeNull();
    });

    it('rebounds from a row to everything that actor did', async () => {
      setup();
      setEntries([auditEntryView({ actorUserId: 'u9' })]);

      el('actor-link')!.click();
      await fixture.whenStable();

      expect(auditService.search).toHaveBeenLastCalledWith('o1', { actorUserId: 'u9' }, 0);
    });

    it('rebounds from a row to the full history of one object', async () => {
      setup();
      setEntries([auditEntryView({ resourceType: 'run', resourceId: 'r42' })]);

      el('resource-link')!.click();
      await fixture.whenStable();

      // Le type ET l'identifiant : le même identifiant peut désigner un objet d'un autre type.
      expect(auditService.search).toHaveBeenLastCalledWith(
        'o1',
        { resourceType: 'run', resourceId: 'r42' },
        0,
      );
    });
  });

  describe('paging', () => {
    it('situates the page inside the filtered total', () => {
      setup();
      auditService.skip.set(50);
      setEntries([auditEntryView({ id: 'a1' }), auditEntryView({ id: 'a2' })], 812);

      // Les bornes sont assertées sur les signaux et non sur le texte rendu : le service de
      // traduction n'a aucun catalogue chargé en test, et n'interpole donc pas les paramètres.
      expect(fixture.componentInstance.rangeStart()).toBe(51);
      expect(fixture.componentInstance.rangeEnd()).toBe(52);
      expect(fixture.componentInstance.total()).toBe(812);
      expect(el('audit-range')).not.toBeNull();
    });

    it('disables the previous button on the first page and the next one at the end', () => {
      setup();
      setEntries([auditEntryView()], 1);

      expect((el('prev-page') as HTMLButtonElement).disabled).toBe(true);
      expect((el('next-page') as HTMLButtonElement).disabled).toBe(true);
    });

    it('walks forward and back by the page size the server applied', async () => {
      setup();
      auditService.take.set(50);
      setEntries([auditEntryView()], 300);

      el('next-page')!.click();
      await fixture.whenStable();
      expect(auditService.search).toHaveBeenLastCalledWith('o1', {}, 50);

      auditService.skip.set(50);
      fixture.detectChanges();
      el('prev-page')!.click();
      await fixture.whenStable();
      expect(auditService.search).toHaveBeenLastCalledWith('o1', {}, 0);
    });

    it('keeps the applied filters while paging, not the ones being typed', async () => {
      setup();
      fixture.componentInstance.filterForm.patchValue({ action: 'user.updated' });
      await fixture.componentInstance.applyFilters();

      // Saisi mais pas validé : demander la page suivante ne doit pas l'embarquer, sinon on
      // paginerait une requête qu'on n'est pas en train de lire.
      fixture.componentInstance.filterForm.patchValue({ actorUserId: 'u9' });
      setEntries([auditEntryView()], 300);

      el('next-page')!.click();
      await fixture.whenStable();

      expect(auditService.search).toHaveBeenLastCalledWith('o1', { action: 'user.updated' }, 50);
    });
  });

  describe('payloads', () => {
    it('keeps changes and details out of the DOM until the row is expanded', () => {
      setup();
      setEntries([
        auditEntryView({ changes: { role: { from: 'viewer', to: 'owner' } }, details: { ip: '10.0.0.1' } }),
      ]);

      // Repliée, la ligne ne projette rien de la charge : le tableau reste lisible et le contenu
      // n'apparaît pas sans être demandé.
      expect(el('audit-detail')).toBeNull();
      expect(fixture.nativeElement.textContent).not.toContain('10.0.0.1');
    });

    it('renders both payloads once expanded, and folds them back', () => {
      setup();
      setEntries([
        auditEntryView({ changes: { role: { from: 'viewer', to: 'owner' } }, details: { ip: '10.0.0.1' } }),
      ]);

      el('toggle-detail')!.click();
      fixture.detectChanges();

      expect(el('detail-changes')!.textContent).toContain('viewer');
      expect(el('detail-changes')!.textContent).toContain('owner');
      expect(el('detail-details')!.textContent).toContain('10.0.0.1');

      el('toggle-detail')!.click();
      fixture.detectChanges();
      expect(el('audit-detail')).toBeNull();
    });

    it('opens one row at a time', () => {
      setup();
      setEntries([
        auditEntryView({ id: 'a1', changes: { a: 1 } }),
        auditEntryView({ id: 'a2', changes: { b: 2 } }),
      ]);

      all('toggle-detail')[0].click();
      fixture.detectChanges();
      all('toggle-detail')[1].click();
      fixture.detectChanges();

      // Deux JSON côte à côte ne se lisent pas.
      expect(all('audit-detail').length).toBe(1);
      expect(el('detail-changes')!.textContent).toContain('b');
    });

    it('offers no detail button on an entry that carries nothing', () => {
      setup();
      setEntries([auditEntryView({ changes: null, details: null })]);

      expect(el('toggle-detail')).toBeNull();
    });
  });

  describe('empty states', () => {
    it('says the log is empty when nothing is filtered', () => {
      setup();
      setEntries([]);

      expect(el('audit-empty')).not.toBeNull();
      expect(el('audit-no-results')).toBeNull();
    });

    it('says no result — not empty log — when a filter is applied', async () => {
      setup();
      fixture.componentInstance.filterForm.patchValue({ action: 'user.updated' });
      await fixture.componentInstance.applyFilters();
      setEntries([]);

      // « Aucun résultat » invite à élargir le filtre ; « journal vide » dit qu'il n'y a rien à
      // chercher. Les confondre ferait douter du filtre.
      expect(el('audit-no-results')).not.toBeNull();
      expect(el('audit-empty')).toBeNull();
    });

    it('hides both while loading', () => {
      setup();
      auditService.isLoading.set(true);
      fixture.detectChanges();

      expect(el('audit-empty')).toBeNull();
      expect(el('audit-spinner')).not.toBeNull();
    });
  });

  describe('role gating', () => {
    /**
     * Le serveur exige `maintainer` sur GET /api/organizations/{orgId}/audit-log et sur ses
     * facettes. La page était rendue à l'identique aux quatre rôles : un developer ou un viewer
     * déclenchait un appel voué au 403, affiché comme une erreur de chargement générique — « ça
     * n'a pas marché » là où la vraie réponse est « ce n'est pas pour vous ».
     */
    it.each<UserRole>(['owner', 'maintainer'])('renders the log to a %s', (role) => {
      setup(role);
      setEntries([auditEntryView({ action: 'user.updated' })]);

      expect(auditService.search).toHaveBeenCalledWith('o1', {});
      expect(rows().length).toBe(1);
      expect(el('audit-forbidden')).toBeNull();
    });

    it.each<UserRole>(['developer', 'viewer'])(
      'tells a %s the log is not theirs, and never asks the server for it',
      (role) => {
        setup(role);
        setEntries([auditEntryView({ action: 'user.updated' })]);

        // Ne pas demander ce qu'on n'a pas le droit de lire : le 403 n'apprend rien à personne.
        expect(auditService.search).not.toHaveBeenCalled();
        expect(auditService.loadFacets).not.toHaveBeenCalled();
        expect(el('audit-forbidden')).not.toBeNull();
        expect(rows().length).toBe(0);
        // Ni filtre ni pagination : la page entière est masquée, pas seulement ses données.
        expect(el('audit-filters')).toBeNull();
      },
    );

    it('never offers a way to alter the log: it is append-only by construction', () => {
      setup('owner');
      setEntries([auditEntryView()]);

      // Aucun bouton de suppression ni d'édition — la table est WORM côté base (migration 0008),
      // et l'IHM ne doit pas laisser croire le contraire.
      const labels = [...fixture.nativeElement.querySelectorAll('button')].map((b: HTMLElement) =>
        b.getAttribute('data-testid'),
      );
      expect(labels).not.toContain('delete-entry');
      expect(fixture.nativeElement.querySelector('form[data-testid="audit-filters"]')).not.toBeNull();
      // Le seul formulaire de la page est celui des filtres : rien n'y écrit.
      expect(fixture.nativeElement.querySelectorAll('form').length).toBe(1);
    });
  });
});
