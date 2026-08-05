import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { TranslatePipe } from '@ngx-translate/core';
import { AuthService } from '../../services/auth.service';

type AuthMode = 'login' | 'register';

@Component({
  selector: 'app-login',
  standalone: true,
  imports: [ReactiveFormsModule, TranslatePipe],
  templateUrl: './login.component.html',
  styleUrls: ['./login.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class LoginComponent {
  private readonly fb = inject(FormBuilder);
  private readonly authService = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  readonly mode = signal<AuthMode>('login');
  readonly isSubmitting = signal(false);
  readonly errorKey = signal<string | null>(null);

  /** True once the user has hand-edited the slug field; stops auto-slugify from overwriting it. */
  private slugTouchedManually = false;

  readonly loginForm = this.fb.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  readonly registerForm = this.fb.group({
    orgName: ['', [Validators.required]],
    orgSlug: ['', [Validators.required, Validators.pattern(/^[a-z0-9-]+$/)]],
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required, Validators.minLength(8)]],
    displayName: [''],
  });

  /** Where to land after a successful sign-in: the URL the user was bounced away from, or home. */
  private returnUrl(): string {
    return sanitizeReturnUrl(this.route.snapshot.queryParamMap.get('returnUrl'));
  }

  switchMode(mode: AuthMode): void {
    this.mode.set(mode);
    this.errorKey.set(null);
  }

  onOrgNameInput(value: string): void {
    if (this.slugTouchedManually) return;
    this.registerForm.patchValue({ orgSlug: slugify(value) }, { emitEvent: false });
  }

  onOrgSlugInput(): void {
    this.slugTouchedManually = true;
  }

  async submitLogin(): Promise<void> {
    if (this.loginForm.invalid) {
      this.loginForm.markAllAsTouched();
      return;
    }
    this.isSubmitting.set(true);
    this.errorKey.set(null);
    try {
      const { email, password } = this.loginForm.getRawValue();
      await this.authService.login(email!, password!);
      await this.router.navigateByUrl(this.returnUrl());
    } catch (err) {
      this.errorKey.set(
        err instanceof HttpErrorResponse && err.status === 401
          ? 'login.invalidCredentials'
          : 'login.genericError',
      );
    } finally {
      this.isSubmitting.set(false);
    }
  }

  async submitRegister(): Promise<void> {
    if (this.registerForm.invalid) {
      this.registerForm.markAllAsTouched();
      return;
    }
    this.isSubmitting.set(true);
    this.errorKey.set(null);
    try {
      const { orgName, orgSlug, email, password, displayName } = this.registerForm.getRawValue();
      await this.authService.register(
        orgName!,
        orgSlug!,
        email!,
        password!,
        displayName || undefined,
      );
      await this.router.navigateByUrl(this.returnUrl());
    } catch (err) {
      this.errorKey.set(
        err instanceof HttpErrorResponse && err.status === 409
          ? 'login.orgSlugTaken'
          : 'login.genericError',
      );
    } finally {
      this.isSubmitting.set(false);
    }
  }
}

/**
 * Only same-origin absolute paths are honoured, so a crafted
 * `?returnUrl=https://evil.example` link can't turn the login form into an open redirect.
 */
function sanitizeReturnUrl(value: string | null): string {
  if (!value || !value.startsWith('/') || value.startsWith('//')) {
    return '/';
  }
  return value;
}

function slugify(value: string): string {
  return value
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
}
