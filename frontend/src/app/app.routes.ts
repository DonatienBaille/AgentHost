import { Routes } from '@angular/router';
import { authGuard } from './core/guards/auth.guard';

export const routes: Routes = [
  {
    path: 'login',
    loadComponent: () => import('./components/login/login.component').then((m) => m.LoginComponent),
    title: 'Agent Host — Sign in',
  },
  {
    // Cibles des liens envoyés par courriel. Anonymes par nécessité : celui qui réinitialise son
    // mot de passe n'a plus de session, et l'invité n'a pas encore de compte.
    path: 'reset-password',
    loadComponent: () =>
      import('./components/reset-password/reset-password.component').then(
        (m) => m.ResetPasswordComponent,
      ),
    title: 'Agent Host — Reset password',
  },
  {
    path: 'accept-invitation',
    loadComponent: () =>
      import('./components/accept-invitation/accept-invitation.component').then(
        (m) => m.AcceptInvitationComponent,
      ),
    title: 'Agent Host — Join the organization',
  },
  {
    path: '',
    loadComponent: () =>
      import('./pages/dashboard/dashboard.component').then((m) => m.DashboardComponent),
    title: 'Agent Host',
    canActivate: [authGuard],
  },
  {
    path: 'projects',
    loadComponent: () =>
      import('./pages/projects/projects-list/projects-list.component').then(
        (m) => m.ProjectsListComponent,
      ),
    title: 'Agent Host — Projects',
    canActivate: [authGuard],
  },
  {
    path: 'projects/:id/new-run',
    loadComponent: () =>
      import('./components/new-run-form/new-run-form.component').then(
        (m) => m.NewRunFormComponent,
      ),
    title: 'Agent Host — New run',
    canActivate: [authGuard],
  },
  {
    path: 'projects/:id/agents',
    loadComponent: () =>
      import('./pages/agents/agents-list/agents-list.component').then(
        (m) => m.AgentsListComponent,
      ),
    title: 'Agent Host — Project agents',
    canActivate: [authGuard],
  },
  {
    path: 'projects/:id',
    loadComponent: () =>
      import('./pages/projects/project-detail/project-detail.component').then(
        (m) => m.ProjectDetailComponent,
      ),
    title: 'Agent Host — Project',
    canActivate: [authGuard],
  },
  {
    path: 'agents/:id',
    loadComponent: () =>
      import('./pages/agents/agent-detail/agent-detail.component').then(
        (m) => m.AgentDetailComponent,
      ),
    title: 'Agent Host — Agent',
    canActivate: [authGuard],
  },
  {
    path: 'agents',
    loadComponent: () =>
      import('./pages/agents/agents-list/agents-list.component').then(
        (m) => m.AgentsListComponent,
      ),
    title: 'Agent Host — Agents',
    canActivate: [authGuard],
  },
  {
    path: 'runs/:id',
    loadComponent: () =>
      import('./components/run-detail/run-detail.component').then((m) => m.RunDetailComponent),
    title: 'Agent Host — Run',
    canActivate: [authGuard],
  },
  {
    path: 'runs',
    loadComponent: () =>
      import('./pages/runs/runs-page/runs-page.component').then((m) => m.RunsPageComponent),
    title: 'Agent Host — Runs',
    canActivate: [authGuard],
  },
  {
    path: 'admin/users',
    loadComponent: () =>
      import('./pages/admin/users/users.component').then((m) => m.UsersComponent),
    title: 'Agent Host — Users',
    canActivate: [authGuard],
  },
  {
    path: 'admin/organizations',
    loadComponent: () =>
      import('./pages/admin/organizations/organizations.component').then(
        (m) => m.OrganizationsComponent,
      ),
    title: 'Agent Host — Organizations',
    canActivate: [authGuard],
  },
  {
    path: 'admin/secrets',
    loadComponent: () =>
      import('./pages/admin/secrets/secrets.component').then((m) => m.SecretsComponent),
    title: 'Agent Host — Secrets',
    canActivate: [authGuard],
  },
  {
    path: 'admin/webhooks/:projectId',
    loadComponent: () =>
      import('./pages/admin/webhooks/webhooks.component').then((m) => m.WebhooksComponent),
    title: 'Agent Host — Webhooks',
    canActivate: [authGuard],
  },
  {
    path: 'admin/audit-log',
    loadComponent: () =>
      import('./pages/admin/audit-log/audit-log.component').then((m) => m.AuditLogComponent),
    title: 'Agent Host — Audit log',
    canActivate: [authGuard],
  },
  {
    path: '**',
    redirectTo: '',
  },
];
