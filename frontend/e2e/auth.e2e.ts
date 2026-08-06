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
