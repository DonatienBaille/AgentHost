import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { TranslatePipe } from '@ngx-translate/core';
import { OrganizationService } from '../../../services/organization.service';
import { AuthService } from '../../../services/auth.service';

@Component({
  selector: 'app-admin-organizations',
  standalone: true,
  imports: [ReactiveFormsModule, DatePipe, TranslatePipe],
  templateUrl: './organizations.component.html',
  styleUrls: ['./organizations.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class OrganizationsComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly organizationService = inject(OrganizationService);
  private readonly authService = inject(AuthService);

  readonly organizations = this.organizationService.organizations;
  readonly isLoading = this.organizationService.isLoading;
  readonly error = this.organizationService.error;

  readonly isOwner = this.authService.isOwner;
  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);

  readonly form = this.fb.group({
    name: ['', [Validators.required]],
    slug: ['', [Validators.required, Validators.pattern(/^[a-z0-9-]+$/)]],
    plan: ['free', [Validators.required]],
  });

  ngOnInit(): void {
    this.organizationService.listOrganizations();
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
      const { name, slug, plan } = this.form.getRawValue();
      await this.organizationService.createOrganization({ name: name!, slug: slug!, plan: plan! });
      this.form.reset({ plan: 'free' });
      this.showForm.set(false);
    } catch (err) {
      this.submitError.set('adminOrgs.createError');
    } finally {
      this.isSubmitting.set(false);
    }
  }
}
