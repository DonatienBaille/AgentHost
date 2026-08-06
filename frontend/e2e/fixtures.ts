import { Page, expect } from '@playwright/test';

/**
 * Shared helpers for the end-to-end specs. Everything here drives the real UI — no API shortcuts,
 * because a helper that registers over HTTP would stop proving that the form still works.
 */

/** Unique per call, so specs never collide on a slug or an email in a shared database. */
export function unique(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`;
}

export interface Account {
  orgName: string;
  orgSlug: string;
  email: string;
  password: string;
}

export function newAccount(): Account {
  const slug = unique('org');
  return {
    orgName: `Org ${slug}`,
    orgSlug: slug,
    email: `${slug}@example.test`,
    // Long and unlikely: the API screens weak passwords, and may screen breached ones.
    password: `E2e-${slug}-Passphrase!`,
  };
}

/**
 * Signs up a brand-new organization through the registration form, leaving the browser
 * authenticated. Returns the account so the spec can sign back in as the same user.
 */
export async function register(page: Page, account = newAccount()): Promise<Account> {
  await submitRegistration(page, account);
  await expect(page).toHaveURL(/\/$|\/#/, { timeout: 20_000 });
  return account;
}

/**
 * Fills and submits the registration form without waiting for a navigation — for the cases where
 * the signup is *expected* to be refused and there will be no navigation to wait for.
 */
export async function submitRegistration(page: Page, account: Account): Promise<void> {
  await page.goto('/login');
  // The mode tabs are the only type="button" controls on the page; the submit button, which
  // carries the same label in registration mode, is type="submit".
  await page.locator('button[type="button"]').nth(1).click();

  await page.locator('#reg-org-name').fill(account.orgName);
  // The slug is auto-derived from the name; overwrite it so the value is ours and predictable.
  await page.locator('#reg-org-slug').fill(account.orgSlug);
  await page.locator('#reg-email').fill(account.email);
  await page.locator('#reg-password').fill(account.password);

  await page.locator('form button[type="submit"]').click();
}

/** Signs in an existing account through the login form. */
export async function login(page: Page, account: Account): Promise<void> {
  await page.goto('/login');
  await page.locator('#login-email').fill(account.email);
  await page.locator('#login-password').fill(account.password);
  await page.locator('form button[type="submit"]').click();
}

/** The token the app persists after a successful sign-in. */
export function storedToken(page: Page): Promise<string | null> {
  return page.evaluate(() => localStorage.getItem('agenthost_token'));
}
