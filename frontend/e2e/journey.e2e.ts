import { Page, expect, test } from '@playwright/test';
import { register, unique } from './fixtures';

/**
 * The main journey, end to end against the real API: sign up, create a project, publish an agent
 * from a YAML manifest, launch a run from it, and read the run back.
 *
 * What this deliberately does *not* assert is that the run succeeds. Launching an agent needs a
 * container runtime; without one the run settles into a terminal infrastructure error, which is
 * correct behaviour and still exercises every step the UI owns — creation, navigation, live
 * status. The container path itself is proven on the backend by ContainerLifecycleTests, which CI
 * forces to execute rather than skip.
 *
 * Buttons are addressed by accessible name rather than by CSS: the app shell contributes its own
 * `type="button"` controls (navigation, language switch, sign-out) to every page, so a structural
 * selector matches the chrome as readily as the content.
 */

/** The minimal manifest the backend's parser accepts — same shape as TestData.BuildManifestYaml. */
function manifest(name: string): string {
  return [
    'apiVersion: agenthost.dev/v1',
    'kind: Agent',
    'metadata:',
    `  name: ${name}`,
    `  displayName: ${name}`,
    '  description: End-to-end test agent',
    'spec:',
    '  type: oci',
    '  image: busybox:latest',
    '  runtime:',
    '    profile: standard',
    '    cpu: 1',
    '    memory: 512Mi',
    '    disk: 1Gi',
    '    maxDurationSeconds: 300',
    '  budget:',
    '    defaultMaxUsd: 5',
    '    hardMaxUsd: 10',
  ].join('\n');
}

/** Creates a project through the projects page and returns its id, taken from the URL it links to. */
async function createProject(page: Page, slug: string): Promise<string> {
  await page.goto('/projects');
  await page.getByRole('button', { name: 'Nouveau projet' }).click();

  await page.locator('#p-name').fill(`Project ${slug}`);
  await page.locator('#p-slug').fill(slug);
  await page.locator('#p-description').fill('Created by the end-to-end suite');
  await page.getByRole('button', { name: 'Créer', exact: true }).click();

  const card = page.locator('a[href^="/projects/"]').filter({ hasText: slug });
  await expect(card).toBeVisible({ timeout: 20_000 });

  const href = await card.getAttribute('href');
  return href!.split('/').pop()!;
}

/** Publishes an agent into a project. A draft would not be runnable, so `publish` is checked. */
async function createPublishedAgent(page: Page, projectId: string, slug: string): Promise<void> {
  await page.goto(`/projects/${projectId}/agents`);
  await page.getByRole('button', { name: 'Nouvel agent' }).click();

  await page.locator('#a-name').fill(`Agent ${slug}`);
  await page.locator('#a-slug').fill(slug);
  // Le manifeste s'écrit désormais dans l'éditeur à deux modes ; ce parcours passe par le YAML.
  await page.locator('[data-testid="mode-yaml"]').click();
  await page.locator('#manifest-yaml').fill(manifest(slug));
  // Ciblée par son testid : le formulaire porte maintenant plusieurs cases à cocher.
  await page.locator('[data-testid="publish-immediately"]').check();
  await page.getByRole('button', { name: 'Créer', exact: true }).click();

  const card = page.locator('a[href^="/agents/"]').filter({ hasText: slug });
  await expect(card).toBeVisible({ timeout: 20_000 });
  // Published, not draft — the distinction the run form depends on.
  await expect(card.getByText('Publié')).toBeVisible();
}

/** Launches a run from the project's new-run form and returns the id of the run it created. */
async function launchRun(page: Page, projectId: string, agentSlug: string): Promise<string> {
  await page.goto(`/projects/${projectId}/new-run`);

  const selector = page.locator('#agent-select');
  await expect(selector.locator(`option:has-text("Agent ${agentSlug}")`)).toHaveCount(1, {
    timeout: 20_000,
  });
  await selector.selectOption({ label: `Agent ${agentSlug}` });

  await page.getByRole('button', { name: 'Lancer le run' }).click();

  // Creating a run navigates to its detail page: the ULID in the URL is the proof the API accepted
  // it, and it is the same id the run list must show.
  await expect(page).toHaveURL(/\/runs\/[0-9A-HJKMNP-TV-Z]{26}$/, { timeout: 30_000 });
  return page.url().split('/').pop()!;
}

test.describe('project → agent → run', () => {
  test('an owner can go from an empty account to a launched run', async ({ page }) => {
    await register(page);

    const projectSlug = unique('proj');
    const projectId = await createProject(page, projectSlug);

    const agentSlug = unique('agent');
    await createPublishedAgent(page, projectId, agentSlug);

    const runId = await launchRun(page, projectId, agentSlug);

    await page.goto('/runs');
    await expect(page.locator(`a[href="/runs/${runId}"]`)).toBeVisible({ timeout: 20_000 });
  });

  test('the run detail page reaches a terminal state on its own', async ({ page }) => {
    await register(page);

    const projectSlug = unique('proj');
    const projectId = await createProject(page, projectSlug);
    const agentSlug = unique('agent');
    await createPublishedAgent(page, projectId, agentSlug);
    await launchRun(page, projectId, agentSlug);

    // Whatever the outcome, the run must not sit forever in a provisional state with the page
    // showing nothing — that is the failure mode a user would actually report. With a container
    // runtime this ends in succeeded; without one, in an infrastructure error. Both are terminal,
    // and both must appear without the user reloading.
    await expect(
      page.getByText(/succeeded|failed|infra_error|timed_out|budget_exceeded|cancelled/i).first(),
    ).toBeVisible({ timeout: 60_000 });
  });

  test('a project with no agents says so instead of rendering an empty shell', async ({ page }) => {
    await register(page);
    const projectId = await createProject(page, unique('proj'));

    await page.goto(`/projects/${projectId}/agents`);
    await expect(page.getByText('Aucun agent pour le moment.')).toBeVisible({ timeout: 20_000 });

    await page.goto(`/projects/${projectId}/new-run`);
    await expect(
      page.getByText("Sélectionnez un agent pour afficher le formulaire d'entrées."),
    ).toBeVisible();
  });
});

/**
 * Le tableau de bord de supervision (lot 3), contre la vraie API.
 *
 * Ce que les tests de composants ne peuvent pas attraper : le câblage. Route, lien de navigation,
 * URL des quatre endpoints, forme réelle des réponses — tout cela est stubbé en test unitaire et
 * n'existe qu'ici.
 */
test.describe('supervision', () => {
  test('a fresh organization sees an honest empty dashboard, not a broken one', async ({ page }) => {
    await register(page);
    await page.goto('/monitoring');

    // Zéro run : les compteurs doivent afficher 0, pas rester vides ni montrer NaN.
    await expect(page.getByTestId('stat-runs')).toHaveText('0', { timeout: 20_000 });
    await expect(page.getByTestId('stat-success-rate')).toHaveText('0%');
    await expect(page.getByTestId('agents-empty')).toBeVisible();
    await expect(page.getByTestId('projects-empty')).toBeVisible();
  });

  test('a launched run shows up in the dashboard tables', async ({ page }) => {
    await register(page);
    const projectSlug = unique('proj');
    const projectId = await createProject(page, projectSlug);
    const agentSlug = unique('agent');
    await createPublishedAgent(page, projectId, agentSlug);
    await launchRun(page, projectId, agentSlug);

    await page.goto('/monitoring');

    await expect(page.getByTestId('stat-runs')).toHaveText('1', { timeout: 20_000 });
    // Les deux ventilations viennent de requêtes distinctes : les voir toutes deux prouve que les
    // quatre endpoints répondent, pas seulement le premier.
    await expect(page.getByTestId('agent-row')).toHaveCount(1);
    await expect(page.getByTestId('project-row')).toHaveCount(1);
    await expect(page.getByTestId('agent-row')).toContainText(`Agent ${agentSlug}`);
    await expect(page.getByTestId('project-row')).toContainText(`Project ${projectSlug}`);
  });

  test('the window selector reloads against the server', async ({ page }) => {
    await register(page);
    await page.goto('/monitoring');
    await expect(page.getByTestId('stat-runs')).toBeVisible({ timeout: 20_000 });

    await page.getByTestId('window-7').click();

    // Le sous-titre porte la fenêtre que le serveur a retenue, pas celle demandée.
    await expect(page.getByText(/7 derniers jours/)).toBeVisible({ timeout: 20_000 });
  });
});

/**
 * Le journal d'audit consultable (lot 3), contre la vraie API.
 *
 * Ce que les tests de composants ne peuvent pas attraper : que les filtres arrivent réellement
 * jusqu'au SQL, que les facettes reflètent ce que le journal contient, et que l'acteur soit résolu
 * en nom par la jointure serveur. Tout cela est stubbé en test unitaire.
 *
 * Le parcours crée un utilisateur, parce que c'est l'un des rares gestes de l'IHM qui écrive dans
 * le journal — et le seul dont la trace porte à la fois un acteur, une ressource et une charge
 * `details`.
 */
test.describe('journal d’audit', () => {
  /** Crée un utilisateur depuis la page d'administration ; renvoie son adresse. */
  async function createUser(page: Page, slug: string): Promise<string> {
    const email = `${slug}@example.com`;
    await page.goto('/admin/users');
    await page.getByTestId('new-user').click();

    await page.locator('#u-email').fill(email);
    await page.locator('#u-password').fill('correct-horse-battery-staple');
    await page.locator('#u-display-name').fill(`User ${slug}`);
    await page.getByRole('button', { name: 'Créer', exact: true }).click();

    await expect(page.getByText(email)).toBeVisible({ timeout: 20_000 });
    return email;
  }

  test('a fresh organization sees an empty log, not a broken page', async ({ page }) => {
    await register(page);
    await page.goto('/admin/audit-log');

    await expect(page.getByTestId('audit-empty')).toBeVisible({ timeout: 20_000 });
    // Zéro entrée : la pagination doit être inerte des deux côtés, pas absente.
    await expect(page.getByTestId('prev-page')).toBeDisabled();
    await expect(page.getByTestId('next-page')).toBeDisabled();
  });

  test('an action leaves a trace naming its actor and carrying its payload', async ({ page }) => {
    const owner = await register(page);
    const member = await createUser(page, unique('member'));

    await page.goto('/admin/audit-log');

    const row = page.getByTestId('audit-row');
    await expect(row).toHaveCount(1, { timeout: 20_000 });
    await expect(row).toContainText('user.created');
    // L'acteur est résolu par la jointure serveur : la table ne stocke qu'un ULID, et c'est
    // l'adresse du compte connecté qui doit se retrouver ici.
    await expect(page.getByTestId('actor-link')).toHaveAttribute('title', owner.email);

    // La charge `details` n'entre dans le DOM qu'une fois la ligne dépliée.
    await expect(page.getByTestId('detail-details')).toHaveCount(0);
    await page.getByTestId('toggle-detail').click();
    // `details` porte l'identité de la CIBLE, que rien d'autre ne résout : la jointure de la
    // consultation ne remonte que l'acteur.
    await expect(page.getByTestId('detail-details')).toContainText(member);
  });

  test('the filters reach the server and narrow the log', async ({ page }) => {
    await register(page);
    await createUser(page, unique('member'));
    await page.goto('/admin/audit-log');
    await expect(page.getByTestId('audit-row')).toHaveCount(1, { timeout: 20_000 });

    // Les choix proposés viennent des facettes : « user.created » ne peut y figurer que parce
    // que le serveur l'a rapporté, avec son volume.
    await page.getByTestId('filter-action').selectOption('user.created');
    await page.getByTestId('apply-filters').click();
    await expect(page.getByTestId('audit-row')).toHaveCount(1);

    // Une période antérieure à toute activité : le journal doit dire « aucun résultat », et non
    // « journal vide » — la nuance est ce qui distingue un filtre trop étroit d'un journal vierge.
    await page.getByTestId('filter-from').fill('2020-01-01');
    await page.getByTestId('filter-to').fill('2020-01-02');
    await page.getByTestId('apply-filters').click();
    await expect(page.getByTestId('audit-no-results')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('audit-empty')).toHaveCount(0);

    await page.getByTestId('reset-filters').click();
    await expect(page.getByTestId('audit-row')).toHaveCount(1);
  });
});

/**
 * Les déclencheurs (lot 4), contre la vraie API.
 *
 * Ce que les tests de composants ne peuvent pas attraper : le câblage. Route, lien depuis la fiche
 * projet, URL des endpoints, forme réelle des réponses — et surtout le fait que le secret vienne
 * bien du serveur et ne soit rendu qu'une fois.
 */
test.describe('déclencheurs', () => {
  test('a project with no trigger says so instead of rendering an empty shell', async ({ page }) => {
    await register(page);
    const projectId = await createProject(page, unique('proj'));

    await page.goto(`/projects/${projectId}/triggers`);
    await expect(page.getByTestId('triggers-empty')).toBeVisible({ timeout: 20_000 });
  });

  test('creating a webhook trigger reveals its secret exactly once', async ({ page }) => {
    await register(page);
    const projectSlug = unique('proj');
    const projectId = await createProject(page, projectSlug);
    await createPublishedAgent(page, projectId, unique('agent'));

    await page.goto(`/projects/${projectId}/triggers`);
    await page.getByTestId('new-trigger').click();

    await page.locator('#t-name').fill('Push sur main');
    // L'agent publié est le seul choix proposé : un brouillon n'est pas lançable.
    await page.getByTestId('trigger-agent').selectOption({ index: 1 });
    await page.locator('#t-branches').fill('main');
    await page.getByTestId('submit-trigger').click();

    // Le secret vient du serveur, qui ne le rendra plus jamais.
    await expect(page.getByTestId('secret-value')).toBeVisible({ timeout: 20_000 });
    const secret = (await page.getByTestId('secret-value').textContent())!.trim();
    expect(secret.length).toBeGreaterThan(20);
    await expect(page.getByTestId('hook-url')).toContainText('/api/hooks/');

    await page.getByTestId('dismiss-secret').click();
    await expect(page.getByTestId('revealed-secret')).toHaveCount(0);

    // Rechargée, la page montre le déclencheur — et plus le secret.
    await page.reload();
    await expect(page.getByTestId('trigger-row')).toHaveCount(1, { timeout: 20_000 });
    await expect(page.locator('body')).not.toContainText(secret);
  });

  test('an unusable cron expression is refused with the reason, not a generic failure', async ({ page }) => {
    await register(page);
    const projectId = await createProject(page, unique('proj'));
    await createPublishedAgent(page, projectId, unique('agent'));

    await page.goto(`/projects/${projectId}/triggers`);
    await page.getByTestId('new-trigger').click();
    await page.locator('#t-name').fill('Planification impossible');
    await page.getByTestId('trigger-agent').selectOption({ index: 1 });
    await page.getByTestId('type-cron').click();
    await page.locator('#t-cron').fill('tous les lundis');
    await page.getByTestId('submit-trigger').click();

    // Le message du serveur nomme ce qu'il n'a pas compris : c'est le seul moyen de corriger.
    await expect(page.getByTestId('submit-error')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('submit-error')).toContainText(/cron|field|value/i);
  });

  test('a cron trigger shows the next occurrence the server computed', async ({ page }) => {
    await register(page);
    const projectId = await createProject(page, unique('proj'));
    await createPublishedAgent(page, projectId, unique('agent'));

    await page.goto(`/projects/${projectId}/triggers`);
    await page.getByTestId('new-trigger').click();
    await page.locator('#t-name').fill('Rapport quotidien');
    await page.getByTestId('trigger-agent').selectOption({ index: 1 });
    await page.getByTestId('type-cron').click();
    await page.locator('#t-cron').fill('0 9 * * *');
    await page.locator('#t-tz').fill('Europe/Paris');
    await page.getByTestId('submit-trigger').click();

    const row = page.getByTestId('trigger-row');
    await expect(row).toHaveCount(1, { timeout: 20_000 });
    await expect(row).toContainText('0 9 * * *');
    await expect(row).toContainText('Europe/Paris');
    // L'échéance est calculée par le serveur à l'écriture : la voir ici prouve qu'elle a été
    // persistée, pas seulement acceptée.
    await expect(page.getByTestId('trigger-next-run')).toBeVisible();

    // Désactiver efface l'échéance : la laisser ferait repartir le déclencheur au réveil avec
    // toutes les occurrences manquées.
    await page.getByTestId('toggle-trigger').click();
    await expect(page.getByTestId('trigger-inactive')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('trigger-next-run')).toHaveCount(0);
  });
});
