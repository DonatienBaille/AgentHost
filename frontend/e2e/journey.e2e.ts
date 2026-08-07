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
