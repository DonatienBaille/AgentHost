import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  inject,
  signal,
} from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { TranslatePipe } from '@ngx-translate/core';
import { UserService } from '../../../services/user.service';
import { AuthService } from '../../../services/auth.service';
import { UserRole } from '../../../core/models';

@Component({
  selector: 'app-admin-users',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, TranslatePipe],
  templateUrl: './users.component.html',
  styleUrls: ['./users.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class UsersComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly userService = inject(UserService);
  private readonly authService = inject(AuthService);

  readonly users = this.userService.users;
  readonly isLoading = this.userService.isLoading;
  readonly error = this.userService.error;

  readonly roles: UserRole[] = ['owner', 'maintainer', 'developer', 'viewer'];

  /**
   * Le serveur exige `maintainer` sur POST/PUT/DELETE /api/users
   * (backend Endpoints/UserEndpoints.cs). `AuthService` était injecté ici sans jamais être
   * utilisé : la barrière était prévue et n'a jamais été posée, si bien qu'un `viewer` remplissait
   * un formulaire de création pour ne récolter qu'un 403.
   */
  readonly canManage = this.authService.isMaintainerOrAbove;

  /** L'utilisateur connecté, pour ne pas lui proposer de se supprimer ou de se rétrograder. */
  readonly currentUserId = computed(() => this.authService.currentUser()?.id ?? null);

  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);

  /** Ligne en cours de modification de rôle, pour n'ouvrir qu'un sélecteur à la fois. */
  readonly editingUserId = signal<string | null>(null);
  readonly rowError = signal<string | null>(null);
  readonly busyUserId = signal<string | null>(null);

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    displayName: [''],
    role: ['developer' as UserRole, [Validators.required]],
  });

  ngOnInit(): void {
    this.userService.listUsers();
  }

  toggleForm(): void {
    this.showForm.set(!this.showForm());
  }

  async submit(): Promise<void> {
    // Revérifié ici et pas seulement dans le gabarit : le `@if` cache le bouton, il n'empêche pas
    // d'atteindre la méthode. Le serveur reste l'autorité — c'est de la défense en profondeur.
    if (!this.canManage()) return;

    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.submitError.set(null);
    try {
      const { email, password, displayName, role } = this.form.getRawValue();
      await this.userService.createUser({
        email: email!,
        password: password!,
        displayName: displayName || undefined,
        role: role!,
      });
      this.form.reset({ role: 'developer' });
      this.showForm.set(false);
    } catch (err) {
      this.submitError.set('adminUsers.createError');
    } finally {
      this.isSubmitting.set(false);
    }
  }

  /** Ouvre (ou referme) le sélecteur de rôle d'une ligne. */
  toggleRoleEditor(userId: string): void {
    this.rowError.set(null);
    this.editingUserId.set(this.editingUserId() === userId ? null : userId);
  }

  async changeRole(userId: string, role: string): Promise<void> {
    if (!this.canManage()) return;
    // Se rétrograder soi-même coûterait à l'organisation son dernier administrateur sans qu'aucun
    // écran ne le dise. Le serveur l'accepterait ; on ne le propose pas.
    if (userId === this.currentUserId()) return;

    this.busyUserId.set(userId);
    this.rowError.set(null);
    try {
      await this.userService.updateUser(userId, { role: role as UserRole });
      this.editingUserId.set(null);
    } catch {
      this.rowError.set('adminUsers.updateError');
    } finally {
      this.busyUserId.set(null);
    }
  }

  async remove(userId: string): Promise<void> {
    if (!this.canManage()) return;
    if (userId === this.currentUserId()) return;

    this.busyUserId.set(userId);
    this.rowError.set(null);
    try {
      await this.userService.deleteUser(userId);
    } catch {
      this.rowError.set('adminUsers.deleteError');
    } finally {
      this.busyUserId.set(null);
    }
  }
}
