import { Routes } from '@angular/router';

export const routes: Routes = [
  {
    path: '',
    loadComponent: () =>
      import('./pages/dashboard/dashboard.component').then((m) => m.DashboardComponent),
    title: 'Agent Host',
  },
  {
    path: 'projects/:id/new-run',
    loadComponent: () =>
      import('./components/new-run-form/new-run-form.component').then(
        (m) => m.NewRunFormComponent,
      ),
    title: 'Agent Host — New run',
  },
  {
    path: 'projects/:id',
    loadComponent: () =>
      import('./pages/projects/project-detail/project-detail.component').then(
        (m) => m.ProjectDetailComponent,
      ),
    title: 'Agent Host — Project',
  },
  {
    path: 'runs/:id',
    loadComponent: () =>
      import('./components/run-detail/run-detail.component').then((m) => m.RunDetailComponent),
    title: 'Agent Host — Run',
  },
  {
    path: 'runs',
    loadComponent: () =>
      import('./pages/runs/runs-page/runs-page.component').then((m) => m.RunsPageComponent),
    title: 'Agent Host — Runs',
  },
  {
    path: '**',
    redirectTo: '',
  },
];
