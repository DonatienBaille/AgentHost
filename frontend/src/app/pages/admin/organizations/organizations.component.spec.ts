import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { OrganizationsComponent } from './organizations.component';
import { OrganizationService } from '../../../services/organization.service';
import { AuthService } from '../../../services/auth.service';
import { CreateOrganizationRequest, Organization, UserRole } from '../../../core/models';
import { organization, user } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class OrganizationServiceStub {
  readonly organizations = signal<Organization[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listOrganizations = vi.fn(async () => {});
  readonly createOrganization = vi.fn(async (req: CreateOrganizationRequest) =>
    organization({ name: req.name, slug: req.slug }),
  );
}

describe('OrganizationsComponent', () => {
  let fixture: ComponentFixture<OrganizationsComponent>;
  let component: OrganizationsComponent;
  let orgService: OrganizationServiceStub;
  let authService: AuthService;

  function setup(role: UserRole | null = 'owner'): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [OrganizationsComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: OrganizationService, useClass: OrganizationServiceStub },
      ],
    });

    orgService = TestBed.inject(OrganizationService) as unknown as OrganizationServiceStub;
    authService = TestBed.inject(AuthService);
    authService.currentUser.set(role ? user(role) : null);

    fixture = TestBed.createComponent(OrganizationsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function rows(): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll('[data-testid="org-row"]');
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('list states', () => {
    it('loads the organizations on init, whatever the role', () => {
      setup('viewer');
      expect(orgService.listOrganizations).toHaveBeenCalledTimes(1);
    });

    it('shows the empty state and no rows when the list comes back empty', () => {
      setup();

      expect(rows().length).toBe(0);
      expect(el('orgs-empty')).not.toBeNull();
    });

    it('hides the empty state while loading', () => {
      setup();
      orgService.isLoading.set(true);
      fixture.detectChanges();

      expect(el('orgs-empty')).toBeNull();
    });

    it('renders the service error message', () => {
      setup();
      orgService.error.set('Failed to load organizations');
      fixture.detectChanges();

      expect(el('orgs-error')!.textContent).toContain('Failed to load organizations');
    });

    it('renders one row per organization with the displayed fields', () => {
      setup();
      orgService.organizations.set([
        organization({ id: 'o1', name: 'Acme', slug: 'acme', plan: 'team' }),
        organization({ id: 'o2', name: 'Globex', slug: 'globex', plan: 'free' }),
      ]);
      fixture.detectChanges();

      expect(rows().length).toBe(2);
      expect(el('orgs-empty')).toBeNull();

      const first = rows()[0].querySelectorAll('td');
      expect(first[0].textContent!.trim()).toBe('Acme');
      expect(first[1].textContent!.trim()).toBe('acme');
      expect(first[2].textContent!.trim()).toBe('team');

      expect(rows()[1].querySelectorAll('td')[0].textContent!.trim()).toBe('Globex');
    });
  });

  describe('role gating', () => {
    it('offers the create button to an owner and no owner-only notice', () => {
      setup('owner');

      expect(el('new-org')).not.toBeNull();
      expect(el('orgs-owner-only')).toBeNull();
    });

    it.each<UserRole>(['maintainer', 'developer', 'viewer'])(
      'hides the create button from a %s and explains why',
      (role) => {
        setup(role);

        expect(el('new-org')).toBeNull();
        expect(el('orgs-owner-only')).not.toBeNull();
      },
    );

    it('keeps the form out of the DOM for a non-owner even if showForm is forced', () => {
      setup('maintainer');

      component.toggleForm();
      fixture.detectChanges();

      expect(component.showForm()).toBe(true);
      expect(fixture.nativeElement.querySelector('#o-name')).toBeNull();
      expect(fixture.nativeElement.querySelector('#o-slug')).toBeNull();
    });

    it('treats a signed-out user as a non-owner', () => {
      setup(null);

      expect(el('new-org')).toBeNull();
      expect(el('orgs-owner-only')).not.toBeNull();
    });

    it('shows the form to an owner once toggled', () => {
      setup('owner');

      component.toggleForm();
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('#o-name')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('#o-plan')).not.toBeNull();
    });
  });

  describe('create payload', () => {
    it('sends name, slug and plan exactly as typed', async () => {
      setup('owner');
      component.form.setValue({ name: 'Globex', slug: 'globex', plan: 'business' });

      await component.submit();

      expect(orgService.createOrganization).toHaveBeenCalledTimes(1);
      expect(orgService.createOrganization).toHaveBeenCalledWith({
        name: 'Globex',
        slug: 'globex',
        plan: 'business',
      });
      const sent = orgService.createOrganization.mock.calls[0][0] as unknown as Record<
        string,
        unknown
      >;
      expect(Object.keys(sent).sort()).toEqual(['name', 'plan', 'slug']);
    });

    it('refuses a slug that is not lowercase-kebab', async () => {
      setup('owner');
      component.form.setValue({ name: 'Globex', slug: 'Globex Inc', plan: 'free' });

      await component.submit();

      expect(orgService.createOrganization).not.toHaveBeenCalled();
      expect(component.form.touched).toBe(true);
    });

    it('closes the form and restores the free plan after a successful create', async () => {
      setup('owner');
      component.toggleForm();
      component.form.setValue({ name: 'Globex', slug: 'globex', plan: 'enterprise' });

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(false);
      expect(component.form.getRawValue().plan).toBe('free');
      expect(fixture.nativeElement.querySelector('#o-name')).toBeNull();
    });

    it('surfaces a create failure without closing the form', async () => {
      setup('owner');
      component.toggleForm();
      orgService.createOrganization.mockRejectedValueOnce(new Error('boom'));
      component.form.setValue({ name: 'Globex', slug: 'globex', plan: 'free' });

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(true);
      expect(component.isSubmitting()).toBe(false);
      expect(fixture.nativeElement.textContent).toContain('adminOrgs.createError');
    });
  });
});
