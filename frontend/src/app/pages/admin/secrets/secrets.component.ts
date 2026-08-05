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

  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);
  readonly deletingId = signal<string | null>(null);

  readonly form = this.fb.group({
    name: ['', [Validators.required]],
    value: ['', [Validators.required]],
    scope: ['org' as SecretScope, [Validators.required]],
    projectId: [''],
  });

  ngOnInit(): void {
    const orgId = this.authService.currentUser()?.orgId;
    if (orgId) {
      this.secretService.listSecrets(orgId);
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
      const { name, value, scope, projectId } = this.form.getRawValue();
      await this.secretService.createSecret({
        orgId,
        name: name!,
        value: value!,
        scope: scope!,
        projectId: scope === 'project' && projectId ? projectId : undefined,
      });
      this.form.reset({ scope: 'org' });
      this.showForm.set(false);
    } catch (err) {
      this.submitError.set('adminSecrets.createError');
    } finally {
      this.isSubmitting.set(false);
    }
  }

  async remove(id: string): Promise<void> {
    this.deletingId.set(id);
    try {
      await this.secretService.deleteSecret(id);
    } catch {
      // surfaced via secretService.error already
    } finally {
      this.deletingId.set(null);
    }
  }
}
