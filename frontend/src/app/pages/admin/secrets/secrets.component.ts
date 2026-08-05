import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { TranslatePipe } from '@ngx-translate/core';
import { SecretService } from '../../../services/secret.service';
import { AuthService } from '../../../services/auth.service';
import { SecretScope } from '../../../core/models';

@Component({
  selector: 'app-admin-secrets',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, TranslatePipe],
  templateUrl: './secrets.component.html',
  styleUrls: ['./secrets.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class SecretsComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly secretService = inject(SecretService);
  private readonly authService = inject(AuthService);

  readonly secrets = this.secretService.secrets;
  readonly isLoading = this.secretService.isLoading;
  readonly error = this.secretService.error;

  /** Every secret route is maintainer+; below that the server 403s, so don't offer the actions. */
  readonly canManage = this.authService.isMaintainerOrAbove;

  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);
  readonly deletingId = signal<string | null>(null);

  /** id of the secret currently being rotated inline, if any. */
  readonly rotatingId = signal<string | null>(null);
  readonly rotateValue = signal('');
  readonly isRotating = signal(false);
  readonly rotateError = signal<string | null>(null);
  readonly rotatedId = signal<string | null>(null);

  readonly form = this.fb.group({
    name: ['', [Validators.required]],
    value: ['', [Validators.required]],
    scope: ['org' as SecretScope, [Validators.required]],
    projectId: [''],
  });

  ngOnInit(): void {
    if (this.canManage()) {
      this.secretService.listSecrets();
    }
  }

  reload(): void {
    this.secretService.listSecrets();
  }

  toggleForm(): void {
    this.showForm.set(!this.showForm());
  }

  async submit(): Promise<void> {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.isSubmitting.set(true);
    this.submitError.set(null);
    try {
      const { name, value, scope, projectId } = this.form.getRawValue();
      await this.secretService.createSecret({
        name: name!,
        value: value!,
        scope: scope!,
        projectId: scope === 'project' && projectId ? projectId : undefined,
      });
      this.form.reset({ scope: 'org' });
      this.showForm.set(false);
    } catch {
      this.submitError.set('adminSecrets.createError');
    } finally {
      this.isSubmitting.set(false);
    }
  }

  startRotate(id: string): void {
    this.rotatingId.set(id);
    this.rotateValue.set('');
    this.rotateError.set(null);
    this.rotatedId.set(null);
  }

  cancelRotate(): void {
    this.rotatingId.set(null);
    this.rotateValue.set('');
    this.rotateError.set(null);
  }

  onRotateValueInput(value: string): void {
    this.rotateValue.set(value);
  }

  async confirmRotate(): Promise<void> {
    const id = this.rotatingId();
    const value = this.rotateValue();
    if (!id || !value) {
      return;
    }

    this.isRotating.set(true);
    this.rotateError.set(null);
    try {
      await this.secretService.rotateSecret(id, value);
      this.rotatingId.set(null);
      this.rotateValue.set('');
      this.rotatedId.set(id);
    } catch {
      this.rotateError.set('adminSecrets.rotateError');
    } finally {
      this.isRotating.set(false);
    }
  }

  async remove(id: string): Promise<void> {
    this.deletingId.set(id);
    try {
      await this.secretService.deleteSecret(id);
    } catch {
      // surfaced globally by the error interceptor
    } finally {
      this.deletingId.set(null);
    }
  }
}
