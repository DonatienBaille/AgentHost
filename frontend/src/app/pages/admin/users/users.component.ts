import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
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

  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);

  readonly form = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    displayName: [''],
    role: ['developer' as UserRole, [Validators.required]],
  });

  ngOnInit(): void {
    const orgId = this.authService.currentUser()?.orgId;
    if (orgId) {
      this.userService.listUsers(orgId);
    }
  }

  toggleForm(): void {
    this.showForm.set(!this.showForm());
  }

  async submit(): Promise<void> {
    const orgId = this.authService.currentUser()?.orgId;
    if (!orgId || this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.submitError.set(null);
    try {
      const { email, password, displayName, role } = this.form.getRawValue();
      await this.userService.createUser({
        orgId,
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
}
