import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideTranslateService } from '@ngx-translate/core';
import { signal } from '@angular/core';
import { ProjectsListComponent } from './projects-list.component';
import { ProjectService } from '../../../services/project.service';
import { AuthService } from '../../../services/auth.service';
import { CreateProjectRequest, Project, UserRole } from '../../../core/models';
import { project, user } from '../../../core/testing/fixtures';

/** Double du service : mêmes signaux que le vrai, aucun HTTP. */
class ProjectServiceStub {
  readonly projects = signal<Project[]>([]);
  readonly isLoading = signal(false);
  readonly error = signal<string | null>(null);
  readonly listProjects = vi.fn(async () => {});
  readonly createProject = vi.fn(async (req: CreateProjectRequest) =>
    project({ name: req.name, slug: req.slug }),
  );
}

describe('ProjectsListComponent', () => {
  let fixture: ComponentFixture<ProjectsListComponent>;
  let component: ProjectsListComponent;
  let projectService: ProjectServiceStub;
  let authService: AuthService;

  function setup(role: UserRole | null = 'developer'): void {
    localStorage.clear();
    TestBed.configureTestingModule({
      imports: [ProjectsListComponent],
      providers: [
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideTranslateService({ lang: 'fr', fallbackLang: 'fr' }),
        { provide: ProjectService, useClass: ProjectServiceStub },
      ],
    });

    projectService = TestBed.inject(ProjectService) as unknown as ProjectServiceStub;
    authService = TestBed.inject(AuthService);
    authService.currentUser.set(role ? user(role) : null);

    fixture = TestBed.createComponent(ProjectsListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function all(testId: string): NodeListOf<HTMLElement> {
    return fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`);
  }

  /** Remplit le formulaire avec un jeu valide, surchargé au besoin. */
  function fillForm(overrides: Partial<Record<string, unknown>> = {}): void {
    component.form.setValue({
      name: 'Site vitrine',
      slug: 'site-vitrine',
      description: 'Refonte du portail',
      budgetMonthlyUsd: 250,
      ...overrides,
    } as never);
  }

  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });

  describe('list states', () => {
    it('loads the projects once on init', () => {
      setup();

      expect(projectService.listProjects).toHaveBeenCalledTimes(1);
    });

    it('shows the empty state and no card when the list comes back empty', () => {
      setup();

      expect(all('project-card').length).toBe(0);
      expect(el('projects-empty')).not.toBeNull();
    });

    it('hides the empty state while loading, so it never flashes', () => {
      setup();
      projectService.isLoading.set(true);
      fixture.detectChanges();

      expect(el('projects-empty')).toBeNull();
      expect(fixture.nativeElement.querySelector('.animate-spin')).not.toBeNull();
    });

    it('renders the error signal of the service', () => {
      setup();
      projectService.error.set('errors.loadProjects');
      fixture.detectChanges();

      const banner = el('projects-error');
      expect(banner).not.toBeNull();
      expect(banner!.textContent).toContain('errors.loadProjects');
    });

    it('renders one card per project with the displayed fields and its link', () => {
      setup();
      projectService.projects.set([
        project({ id: 'p1', name: 'Site vitrine', slug: 'site-vitrine', description: null }),
        project({ id: 'p2', name: 'API interne', slug: 'api-interne', description: 'Back-office' }),
      ]);
      fixture.detectChanges();

      const cards = all('project-card');
      expect(cards.length).toBe(2);
      expect(el('projects-empty')).toBeNull();

      expect(cards[0].textContent).toContain('Site vitrine');
      expect(cards[0].textContent).toContain('site-vitrine');
      expect(cards[0].getAttribute('href')).toBe('/projects/p1');

      expect(cards[1].textContent).toContain('Back-office');
      expect(cards[1].getAttribute('href')).toBe('/projects/p2');
    });

    it('omits the description block when the project has none', () => {
      setup();
      projectService.projects.set([project({ description: null })]);
      fixture.detectChanges();

      // Nom + slug seulement : pas de troisième ligne vide.
      expect(all('project-card')[0].querySelectorAll('div').length).toBe(2);
    });

    it('keeps the cards visible alongside an error banner', () => {
      setup();
      projectService.projects.set([project()]);
      projectService.error.set('errors.loadProjects');
      fixture.detectChanges();

      expect(all('project-card').length).toBe(1);
      expect(el('projects-error')).not.toBeNull();
    });
  });

  describe('role gating', () => {
    it.each<UserRole>(['developer', 'maintainer', 'owner'])(
      'shows the create button to a %s',
      (role) => {
        setup(role);

        expect(component.canCreate()).toBe(true);
        expect(el('new-project')).not.toBeNull();
      },
    );

    it('hides the create button from a viewer', () => {
      setup('viewer');

      expect(component.canCreate()).toBe(false);
      expect(el('new-project')).toBeNull();
      expect(el('project-form')).toBeNull();
    });

    it('hides the create button from a signed-out user', () => {
      setup(null);

      expect(component.canCreate()).toBe(false);
      expect(el('new-project')).toBeNull();
    });

    it('still lists the projects for a viewer: reading is allowed', () => {
      setup('viewer');
      projectService.projects.set([project()]);
      fixture.detectChanges();

      expect(all('project-card').length).toBe(1);
      expect(projectService.listProjects).toHaveBeenCalledTimes(1);
    });

    it('reveals the form only once the create button is clicked', () => {
      setup('developer');
      expect(el('project-form')).toBeNull();

      el('new-project')!.click();
      fixture.detectChanges();

      expect(el('project-form')).not.toBeNull();
    });

    it('refuses a forced submit from a viewer', async () => {
      setup('viewer');
      fillForm();

      // Masquer le bouton n'empêche pas d'appeler la méthode. Le serveur reste l'autorité —
      // cette garde est de la défense en profondeur.
      await component.submit();

      expect(projectService.createProject).not.toHaveBeenCalled();
    });
  });

  describe('create payload', () => {
    it('sends exactly the four fields of CreateProjectRequest', async () => {
      setup('developer');
      fillForm();

      await component.submit();

      expect(projectService.createProject).toHaveBeenCalledWith({
        name: 'Site vitrine',
        slug: 'site-vitrine',
        description: 'Refonte du portail',
        budgetMonthlyUsd: 250,
      });
      // Aucun orgId dans le corps : le serveur le déduit du JWT (CreateProjectRequest).
      const sent = projectService.createProject.mock.calls[0][0] as unknown as Record<
        string,
        unknown
      >;
      expect(Object.keys(sent).sort()).toEqual([
        'budgetMonthlyUsd',
        'description',
        'name',
        'slug',
      ]);
    });

    it('sends the default monthly budget when the field is untouched', async () => {
      setup('developer');
      component.form.patchValue({ name: 'Site vitrine', slug: 'site-vitrine' });

      await component.submit();

      expect(projectService.createProject).toHaveBeenCalledWith(
        expect.objectContaining({ budgetMonthlyUsd: 1000 }),
      );
    });

    it('turns an empty description into undefined rather than an empty string', async () => {
      setup('developer');
      fillForm({ description: '' });

      await component.submit();

      expect(projectService.createProject).toHaveBeenCalledWith(
        expect.objectContaining({ description: undefined }),
      );
    });

    it('turns a cleared budget into undefined rather than null', async () => {
      setup('developer');
      fillForm({ budgetMonthlyUsd: null });

      await component.submit();

      expect(projectService.createProject).toHaveBeenCalledWith(
        expect.objectContaining({ budgetMonthlyUsd: undefined }),
      );
    });

    it('sends a zero budget as-is: zero is a meaningful cap, not a missing value', async () => {
      setup('developer');
      fillForm({ budgetMonthlyUsd: 0 });

      await component.submit();

      expect(projectService.createProject).toHaveBeenCalledWith(
        expect.objectContaining({ budgetMonthlyUsd: 0 }),
      );
    });

    it('closes the form and resets it to the default budget after a success', async () => {
      setup('developer');
      component.toggleForm();
      fillForm();

      await component.submit();

      expect(component.showForm()).toBe(false);
      expect(component.form.getRawValue().budgetMonthlyUsd).toBe(1000);
      expect(component.form.getRawValue().name).toBeNull();
      expect(component.submitError()).toBeNull();
      expect(component.isSubmitting()).toBe(false);
    });
  });

  describe('create validation', () => {
    it('does not call the service when the name is missing', async () => {
      setup('developer');
      fillForm({ name: '' });

      await component.submit();

      expect(projectService.createProject).not.toHaveBeenCalled();
      expect(component.form.controls.name.touched).toBe(true);
    });

    it.each(['Site Vitrine', 'site_vitrine', 'site vitrine', 'sité'])(
      'refuses the non-kebab slug %s',
      async (slug) => {
        setup('developer');
        fillForm({ slug });

        await component.submit();

        expect(projectService.createProject).not.toHaveBeenCalled();
      },
    );

    it('accepts a kebab-case slug with digits', async () => {
      setup('developer');
      fillForm({ slug: 'site-v2' });

      await component.submit();

      expect(projectService.createProject).toHaveBeenCalledWith(
        expect.objectContaining({ slug: 'site-v2' }),
      );
    });
  });

  describe('create failure', () => {
    it('surfaces the error and keeps the form open with its values', async () => {
      setup('developer');
      component.toggleForm();
      projectService.createProject.mockRejectedValueOnce(new Error('boom'));
      fillForm();

      await component.submit();
      fixture.detectChanges();

      expect(component.showForm()).toBe(true);
      expect(component.isSubmitting()).toBe(false);
      expect(component.form.getRawValue().name).toBe('Site vitrine');
      expect(el('project-submit-error')!.textContent).toContain('projectsList.createError');
    });

    it('clears a previous error on the next successful submit', async () => {
      setup('developer');
      projectService.createProject.mockRejectedValueOnce(new Error('boom'));
      fillForm();
      await component.submit();
      expect(component.submitError()).toBe('projectsList.createError');

      fillForm();
      await component.submit();

      expect(component.submitError()).toBeNull();
    });
  });
});
