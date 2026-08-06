import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { UsersComponent } from './users.component';
import { UserService } from '../../../services/user.service';
import { AuthService } from '../../../services/auth.service';
import { CreateUserRequest, UpdateUserRequest, User, UserRole } from '../../../core/models';
import { user } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class UserServiceStub {
  readonly users = signal<User[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listUsers = vi.fn(async () => {});
  readonly createUser = vi.fn(async (req: CreateUserRequest) => user(req.role, { email: req.email }));
  readonly updateUser = vi.fn(async (id: string, req: UpdateUserRequest) =>
    user(req.role ?? 'developer', { id }),
  );
  readonly deleteUser = vi.fn(async (_id: string) => {});
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
      userService.error.set('errors.loadUsers');
      fixture.detectChanges();

      const banner = fixture.nativeElement.querySelector('[data-testid="users-error"]');
      expect(banner).not.toBeNull();
      expect(banner.textContent).toContain('errors.loadUsers');
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
     * Le serveur exige `maintainer` sur POST/PUT/DELETE /api/users. `AuthService` était injecté
     * dans le composant sans jamais être utilisé : la barrière était prévue et n'a jamais été
     * posée, si bien qu'un viewer remplissait un formulaire de création pour ne récolter qu'un 403.
     */
    it.each<UserRole>(['owner', 'maintainer'])('offers the create button to a %s', (role) => {
      setup(role);
      expect(fixture.nativeElement.querySelector('[data-testid="new-user"]')).not.toBeNull();
    });

    it.each<UserRole>(['developer', 'viewer'])(
      'hides the create button and the whole form from a %s',
      (role) => {
        setup(role);
        expect(fixture.nativeElement.querySelector('[data-testid="new-user"]')).toBeNull();

        // Même en forçant l'ouverture : masquer un bouton ne suffit pas si le gabarit rend quand
        // même le formulaire dès que le signal bascule.
        component.toggleForm();
        fixture.detectChanges();
        expect(fixture.nativeElement.querySelector('#u-email')).toBeNull();
        expect(fixture.nativeElement.querySelector('#u-role')).toBeNull();
      },
    );

    it('does not create when an under-privileged user forces submit', async () => {
      setup('viewer');
      component.form.setValue({
        email: 'sneaky@example.com',
        password: 'hunter2hunter2',
        displayName: '',
        role: 'owner',
      });

      await component.submit();

      expect(userService.createUser).not.toHaveBeenCalled();
    });

    it.each<UserRole>(['developer', 'viewer'])('renders no row action for a %s', (role) => {
      setup(role);
      userService.users.set([user('viewer', { id: 'u2' })]);
      fixture.detectChanges();

      expect(rows()[0].querySelector('[data-testid="edit-role"]')).toBeNull();
      expect(rows()[0].querySelector('[data-testid="delete-user"]')).toBeNull();
    });
  });

  describe('row actions', () => {
    it('offers role change and deletion to a maintainer', () => {
      setup('maintainer');
      userService.users.set([user('viewer', { id: 'u2' })]);
      fixture.detectChanges();

      expect(rows()[0].querySelector('[data-testid="edit-role"]')).not.toBeNull();
      expect(rows()[0].querySelector('[data-testid="delete-user"]')).not.toBeNull();
    });

    it('sends only the role on the update, leaving every other field untouched', async () => {
      setup('owner');
      userService.users.set([user('viewer', { id: 'u2' })]);
      fixture.detectChanges();

      await component.changeRole('u2', 'maintainer');

      expect(userService.updateUser).toHaveBeenCalledExactlyOnceWith('u2', { role: 'maintainer' });
    });

    it('closes the role editor once the change lands', async () => {
      setup('owner');
      userService.users.set([user('viewer', { id: 'u2' })]);
      fixture.detectChanges();

      component.toggleRoleEditor('u2');
      expect(component.editingUserId()).toBe('u2');

      await component.changeRole('u2', 'developer');
      expect(component.editingUserId()).toBeNull();
    });

    it('deletes a member through the service', async () => {
      setup('owner');
      userService.users.set([user('viewer', { id: 'u2' })]);
      fixture.detectChanges();

      await component.remove('u2');

      expect(userService.deleteUser).toHaveBeenCalledExactlyOnceWith('u2');
    });

    it('reports a failed update without losing the row', async () => {
      setup('owner');
      userService.users.set([user('viewer', { id: 'u2' })]);
      fixture.detectChanges();
      userService.updateUser.mockRejectedValueOnce(new Error('boom'));

      await component.changeRole('u2', 'owner');
      fixture.detectChanges();

      expect(component.rowError()).toBe('adminUsers.updateError');
      expect(rows().length).toBe(1);
    });

    it('reports a failed deletion', async () => {
      setup('owner');
      userService.users.set([user('viewer', { id: 'u2' })]);
      fixture.detectChanges();
      userService.deleteUser.mockRejectedValueOnce(new Error('boom'));

      await component.remove('u2');
      fixture.detectChanges();

      expect(component.rowError()).toBe('adminUsers.deleteError');
    });

    /**
     * Se rétrograder ou se supprimer soi-même peut priver l'organisation de son dernier
     * administrateur, sans qu'aucun écran ne le dise. Le serveur l'accepterait ; on ne le propose
     * pas, et on refuse même si la méthode est appelée directement.
     */
    it('offers no action on your own row', () => {
      setup('owner');
      userService.users.set([user('owner', { id: 'u1' })]);
      fixture.detectChanges();

      expect(rows()[0].querySelector('[data-testid="self-row"]')).not.toBeNull();
      expect(rows()[0].querySelector('[data-testid="edit-role"]')).toBeNull();
      expect(rows()[0].querySelector('[data-testid="delete-user"]')).toBeNull();
    });

    it('refuses a forced self-demotion or self-deletion', async () => {
      setup('owner');
      userService.users.set([user('owner', { id: 'u1' })]);
      fixture.detectChanges();

      await component.changeRole('u1', 'viewer');
      await component.remove('u1');

      expect(userService.updateUser).not.toHaveBeenCalled();
      expect(userService.deleteUser).not.toHaveBeenCalled();
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
