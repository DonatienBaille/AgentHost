import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { TranslatePipe } from '@ngx-translate/core';
import { ProjectService } from '../../../services/project.service';
import { AuthService } from '../../../services/auth.service';

@Component({
  selector: 'app-projects-list',
  standalone: true,
  imports: [ReactiveFormsModule, RouterLink, TranslatePipe],
  templateUrl: './projects-list.component.html',
  styleUrls: ['./projects-list.component.css'],
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectsListComponent implements OnInit {
  private readonly fb = inject(FormBuilder);
  private readonly projectService = inject(ProjectService);
  private readonly authService = inject(AuthService);

  readonly projects = this.projectService.projects;
  readonly isLoading = this.projectService.isLoading;
  readonly error = this.projectService.error;

  readonly canCreate = this.authService.isDeveloperOrAbove;
  readonly showForm = signal(false);
  readonly isSubmitting = signal(false);
  readonly submitError = signal<string | null>(null);

  readonly form = this.fb.group({
    name: ['', [Validators.required]],
    slug: ['', [Validators.required, Validators.pattern(/^[a-z0-9-]+$/)]],
    description: [''],
    budgetMonthlyUsd: [1000],
  });

  ngOnInit(): void {
    this.projectService.listProjects();
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
      const { name, slug, description, budgetMonthlyUsd } = this.form.getRawValue();
      await this.projectService.createProject({
        orgId,
        name: name!,
        slug: slug!,
        description: description || undefined,
        budgetMonthlyUsd: budgetMonthlyUsd ?? undefined,
      });
      this.form.reset({ budgetMonthlyUsd: 1000 });
      this.showForm.set(false);
    } catch (err) {
      this.submitError.set('projectsList.createError');
    } finally {
      this.isSubmitting.set(false);
    }
  }
}
