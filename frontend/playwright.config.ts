import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end tests against the real stack: a real Postgres, the real .NET API, and the Angular
 * dev server — no mocks anywhere. This is the frontend counterpart of the backend's
 * ContainerLifecycleTests: the component specs prove each piece in isolation, this proves they
 * are wired to each other.
 *
 * Playwright boots both servers itself (see `webServer` below), so `npx playwright test` is the
 * whole command. Postgres is the one thing it expects to already be running — CI provides it as a
 * service container, locally `service postgresql start`.
 */

const API_PORT = Number(process.env.E2E_API_PORT ?? 5000);
const WEB_PORT = Number(process.env.E2E_WEB_PORT ?? 4200);

/**
 * A database of its own by default: these tests register organizations and launch runs, and
 * pointing them at a development database would quietly fill it with fixtures. CI overrides this
 * to its throwaway service container.
 */
const CONNECTION_STRING =
  process.env.E2E_CONNECTION_STRING ??
  'Host=localhost;Port=5432;Database=agenthost_e2e;Username=agenthost;Password=agenthost_dev';

export default defineConfig({
  testDir: './e2e',
  // Without this, Playwright would also collect the Vitest component specs under src/.
  testMatch: /.*\.e2e\.ts/,

  // Registration and run creation are rate-limited server-side and share one database, so these
  // specs run one at a time. Correctness over wall-clock: the suite is small.
  fullyParallel: false,
  workers: 1,

  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: process.env.CI ? [['github'], ['list']] : [['list']],

  use: {
    baseURL: `http://localhost:${WEB_PORT}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },

  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],

  webServer: [
    {
      command: 'dotnet run --project ../backend/src/AgentHost.Api --no-launch-profile',
      url: `http://localhost:${API_PORT}/health`,
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'pipe',
      stderr: 'pipe',
      env: {
        ASPNETCORE_URLS: `http://localhost:${API_PORT}`,
        ConnectionStrings__DefaultConnection: CONNECTION_STRING,
        // Fixed test keys. They are not secret by design — anything real would have to come from
        // the environment, and a deployment that reused these would be broken in ways these tests
        // are not there to catch.
        Secrets__EncryptionKey: '5ijlcr2IxwxqH7PEwsUIGQSvbZ/w6EMuOk3mXorL54c=',
        Jwt__Secret: 'hkA6jtNJGUsnP4++43POCQPqDtH2ABIaIUKj9gXxeJmXnpv5kYxiR9tRYT+JUzke',
        Auth__AllowSelfRegistration: 'true',
        Cors__AllowedOrigins__0: `http://localhost:${WEB_PORT}`,
      },
    },
    {
      // The development configuration, whose `apiUrl` is the API above. The production build uses
      // relative URLs and assumes nginx is reverse-proxying both onto one origin.
      command: `npx ng serve --port ${WEB_PORT}`,
      url: `http://localhost:${WEB_PORT}`,
      timeout: 180_000,
      reuseExistingServer: !process.env.CI,
      stdout: 'pipe',
      stderr: 'pipe',
    },
  ],
});
