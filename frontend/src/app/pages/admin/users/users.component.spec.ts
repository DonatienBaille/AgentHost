import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { UsersComponent } from './users.component';
import { UserService } from '../../../services/user.service';
import { AuthService } from '../../../services/auth.service';
import { CreateUserRequest, User, UserRole } from '../../../core/models';
import { user } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class UserServiceStub {
  readonly users = signal<User[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listUsers = vi.fn(async () => {});
  readonly createUser = vi.fn(async (req: CreateUserRequest) => user(req.role, { email: req.email }));
}

describe('UsersComponent', () => {
  let fixture: ComponentFixture<UsersComponent>;
  let component: UsersComponent;
  let userService: UserServiceStub;
  let authService: AuthService;

  function setup(role: UserRole | null = 'owner'): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [UsersComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: UserService, useClass: UserServiceStub },
      ],
    });

    userService = TestBed.inject(UserService) as unknown as UserServiceStub;
    authService = TestBed.inject(AuthService);
    authService.currentUser.set(role ? user(role) : null);

    fixture = TestBed.createComponent(UsersComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function rows(): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll('[data-testid="user-row"]');
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('list states', () => {
    it('asks the service for the org users on init', () => {
      setup();
      expect(userService.listUsers).toHaveBeenCalledTimes(1);
    });

    it('shows the empty state and no rows when the list comes back empty', () => {
      setup();

      expect(rows().length).toBe(0);
      expect(fixture.nativeElement.querySelector('[data-testid="users-empty"]')).not.toBeNull();
    });

    it('hides the empty state while loading', () => {
      setup();
      userService.isLoading.set(true);
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[data-testid="users-empty"]')).toBeNull();
    });

    it('renders the service error message', () => {
      setup();
      userService.error.set('Failed to load users');
      fixture.detectChanges();

      const banner = fixture.nativeElement.querySelector('[data-testid="users-error"]');
      expect(banner).not.toBeNull();
      expect(banner.textContent).toContain('Failed to load users');
    });

    it('renders one row per user with the displayed fields', () => {
      setup();
      userService.users.set([
        user('owner', { id: 'u1', email: 'ada@example.com', displayName: 'Ada' }),
        user('viewer', { id: 'u2', email: 'bob@example.com', displayName: null }),
      ]);
      fixture.detectChanges();

      expect(rows().length).toBe(2);
      expect(fixture.nativeElement.querySelector('[data-testid="users-empty"]')).toBeNull();

      const first = rows()[0].querySelectorAll('td');
      expect(first[0].textContent!.trim()).toBe('ada@example.com');
      expect(first[1].textContent!.trim()).toBe('Ada');
      // Clé non résolue en test : ngx-translate rend la clé telle quelle.
      expect(first[2].textContent!.trim()).toBe('roles.owner');

      const second = rows()[1].querySelectorAll('td');
      // Un displayName nul retombe sur un tiret cadratin, jamais sur "null".
      expect(second[1].textContent!.trim()).toBe('—');
      expect(second[2].textContent!.trim()).toBe('roles.viewer');
    });
  });

  describe('role gating', () => {
    /**
     * MANQUE CONSTATÉ : la page n'a aucune barrière de rôle. POST /api/users est réservé au
     * serveur aux rôles élevés, mais le bouton « nouvel utilisateur » et son formulaire sont
     * offerts à tout le monde, y compris un viewer — qui ne récoltera qu'un 403.
     * Ces deux tests épinglent le comportement ACTUEL ; ils devront être inversés lorsque la
     * barrière sera ajoutée.
     */
    it('offers the create button to an owner', () => {
      setup('owner');
      expect(fixture.nativeElement.querySelector('[data-testid="new-user"]')).not.toBeNull();
    });

    it('also offers the create button to a viewer (missing role gate)', () => {
      setup('viewer');
      expect(fixture.nativeElement.querySelector('[data-testid="new-user"]')).not.toBeNull();

      component.toggleForm();
      fixture.detectChanges();
      // Le formulaire complet est accessible à un viewer.
      expect(fixture.nativeElement.querySelector('#u-email')).not.toBeNull();
      expect(fixture.nativeElement.querySelector('#u-role')).not.toBeNull();
    });

    it('renders no role-changing control on the rows at all', () => {
      setup('owner');
      userService.users.set([user('viewer', { id: 'u2' })]);
      fixture.detectChanges();

      // MANQUE CONSTATÉ : aucune action par ligne (changer de rôle, supprimer) n'existe encore,
      // donc rien à masquer côté rôle. Test de régression pour le jour où elles arriveront.
      expect(rows()[0].querySelectorAll('button, select').length).toBe(0);
    });
  });

  describe('create payload', () => {
    it('sends every contract field, with the display name when provided', async () => {
      setup('owner');
      component.toggleForm();
      component.form.setValue({
        email: 'new@example.com',
        password: 'hunter2hunter2',
        displayName: 'New Person',
        role: 'maintainer',
      });

      await component.submit();

      expect(userService.createUser).toHaveBeenCalledTimes(1);
      expect(userService.createUser).toHaveBeenCalledWith({
        email: 'new@example.com',
        password: 'hunter2hunter2',
        displayName: 'New Person',
        role: 'maintainer',
      });
      // Aucun orgId dans le corps : le serveur le déduit du JWT (CreateUserRequest).
      const sent = userService.createUser.mock.calls[0][0] as unknown as Record<string, unknown>;
      expect(Object.keys(sent).sort()).toEqual(['displayName', 'email', 'password', 'role']);
    });

    it('omits an empty display name rather than sending an empty string', async () => {
      setup('owner');
      component.form.setValue({
        email: 'new@example.com',
        password: 'hunter2hunter2',
        displayName: '',
        role: 'developer',
      });

      await component.submit();

      expect(userService.createUser).toHaveBeenCalledWith({
        email: 'new@example.com',
        password: 'hunter2hunter2',
        displayName: undefined,
        role: 'developer',
      });
    });

    it('does not call the service when the form is invalid', async () => {
      setup('owner');
      component.form.setValue({
        email: 'not-an-email',
        password: 'short',
        displayName: '',
        role: 'developer',
      });

      await component.submit();

      expect(userService.createUser).not.toHaveBeenCalled();
      expect(component.form.touched).toBe(true);
    });

    it('closes the form and resets the role after a successful create', async () => {
      setup('owner');
      component.toggleForm();
      component.form.setValue({
        email: 'new@example.com',
        password: 'hunter2hunter2',
        displayName: 'X',
        role: 'owner',
      });

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(false);
      expect(component.form.getRawValue().role).toBe('developer');
      expect(fixture.nativeElement.querySelector('#u-email')).toBeNull();
    });

    it('surfaces a create failure without closing the form', async () => {
      setup('owner');
      component.toggleForm();
      userService.createUser.mockRejectedValueOnce(new Error('boom'));
      component.form.setValue({
        email: 'new@example.com',
        password: 'hunter2hunter2',
        displayName: '',
        role: 'developer',
      });

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(true);
      expect(component.isSubmitting()).toBe(false);
      expect(fixture.nativeElement.textContent).toContain('adminUsers.createError');
    });
  });
});
