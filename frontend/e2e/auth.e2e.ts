import { expect, test } from '@playwright/test';
import { login, newAccount, register, storedToken, submitRegistration } from './fixtures';

/**
 * The authentication journey against the real API: signup creates a real organization row, the
 * JWT is a real one, and the route guard is the real guard. Everything the component specs stub
 * out is live here.
 */
test.describe('authentication', () => {
  test('an anonymous visitor is sent to /login and back to where they were headed', async ({
    page,
  }) => {
    await page.goto('/runs');

    // The guard bounces and remembers the attempted URL, so signing in does not dump the user on
    // the dashboard having forgotten what they clicked.
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fruns$/);

    const account = newAccount();
    await page.goto('/login');
    await register(page, account);
    await page.context().clearCookies();
    await page.evaluate(() => localStorage.clear());

    await page.goto('/runs');
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fruns$/);
    await page.locator('#login-email').fill(account.email);
    await page.locator('#login-password').fill(account.password);
    await page.locator('form button[type="submit"]').click();

    await expect(page).toHaveURL(/\/runs$/, { timeout: 20_000 });
  });

  test('registration creates an organization and lands on the dashboard authenticated', async ({
    page,
  }) => {
    const account = await register(page);

    expect(await storedToken(page)).toBeTruthy();

    // The session survives a reload — i.e. it really was persisted, not just held in memory.
    await page.reload();
    await expect(page).not.toHaveURL(/\/login/);
  });

  test('a wrong password is refused with an inline message, not a redirect loop', async ({
    page,
  }) => {
    const account = await register(page);
    await page.evaluate(() => localStorage.clear());

    await login(page, { ...account, password: 'definitely-not-the-password' });

    // The 401 is a *result* here, so the page must not treat it as an expired session.
    await expect(page.getByText(/incorrect|invalid/i)).toBeVisible({ timeout: 20_000 });
    await expect(page).toHaveURL(/\/login/);
    expect(await storedToken(page)).toBeNull();
  });

  test('a slug that is already taken is reported rather than silently failing', async ({ page }) => {
    const account = await register(page);
    await page.evaluate(() => localStorage.clear());

    await submitRegistration(page, { ...account, email: `other-${account.email}` });

    // The inline form error, not the interceptor's global banner: the form is where the user is
    // looking, and it must name the actual cause rather than a generic failure. French is the
    // default language (spec §4.2), so the string is deterministic.
    await expect(page.getByText("Ce slug d'organisation est déjà utilisé.")).toBeVisible({
      timeout: 20_000,
    });
    await expect(page).toHaveURL(/\/login/);
  });

  test('an unauthenticated API session cannot reach a protected page by URL alone', async ({
    page,
  }) => {
    await page.goto('/admin/users');
    await expect(page).toHaveURL(/\/login\?returnUrl=%2Fadmin%2Fusers$/);

    // A forged token must not be enough either: the guard lets it through, the API rejects it, and
    // the error interceptor tears the session down rather than leaving a half-logged-in shell.
    await page.evaluate(() => localStorage.setItem('agenthost_token', 'not.a.real.jwt'));
    await page.goto('/admin/users');
    await expect(page).toHaveURL(/\/login/, { timeout: 20_000 });
  });
});

/**
 * Les deux écrans cibles des liens envoyés par courriel. Ils n'existaient pas quand le mailer a été
 * branché : les liens pointaient vers des routes non implémentées, donc vers la redirection
 * `**` du routeur.
 */
test.describe('écrans des liens de courriel', () => {
  test('the reset screen asks for an address, and says the same thing whatever the answer', async ({
    page,
  }) => {
    const account = await register(page);
    await page.evaluate(() => localStorage.clear());

    await page.goto('/reset-password');
    await expect(page.locator('#reset-email')).toBeVisible();

    // Une adresse qui existe.
    await page.locator('#reset-email').fill(account.email);
    await page.getByTestId('reset-request-submit').click();
    const known = await page.getByTestId('reset-request-sent').textContent();

    // Une adresse qui n'existe pas : le message doit être rigoureusement le même, sinon
    // l'écran devient l'oracle d'énumération que le 202 plat du serveur referme.
    await page.goto('/reset-password');
    await page.locator('#reset-email').fill(`nobody-${Date.now()}@example.test`);
    await page.getByTestId('reset-request-submit').click();
    const unknown = await page.getByTestId('reset-request-sent').textContent();

    expect(known?.trim()).toBeTruthy();
    expect(unknown?.trim()).toBe(known?.trim());
  });

  test('a reset link with a dead token is refused instead of pretending to work', async ({ page }) => {
    await page.goto('/reset-password?token=not-a-real-token');
    await expect(page.locator('#reset-new-password')).toBeVisible();

    await page.locator('#reset-new-password').fill('Some-New-Passphrase-123!');
    await page.getByTestId('reset-submit').click();

    await expect(page.getByTestId('reset-error')).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId('reset-done')).toHaveCount(0);
  });

  test('an invitation link with no token offers no form at all', async ({ page }) => {
    await page.goto('/accept-invitation');

    await expect(page.getByTestId('invitation-missing-token')).toBeVisible();
    // Offrir un champ de mot de passe laisserait croire qu'un compte va être créé.
    await expect(page.locator('#invite-password')).toHaveCount(0);
  });

  test('an invitation link with a dead token is refused', async ({ page }) => {
    await page.goto('/accept-invitation?token=not-a-real-token');
    await page.locator('#invite-password').fill('Some-New-Passphrase-123!');
    await page.getByTestId('invitation-submit').click();

    await expect(page.getByTestId('invitation-error')).toBeVisible({ timeout: 20_000 });
    await expect(page).toHaveURL(/accept-invitation/);
  });
});
