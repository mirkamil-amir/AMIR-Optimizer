import { defineConfig, devices } from '@playwright/test'

/**
 * End-to-end tests against a running stack (API + PostgreSQL + this frontend). They are not self-contained on purpose:
 * start the stack first (see README "Testing"), then run `npm run test:e2e` with E2E_ADMIN_EMAIL / E2E_ADMIN_PASSWORD set
 * to the bootstrap administrator. E2E_BASE_URL defaults to the Vite dev server; use http://localhost:8081 for Compose.
 * The specs create uniquely named users and never delete data.
 */
export default defineConfig({
  testDir: './e2e',
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'playwright-report' }]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  // The API allows 10 sign-in attempts per minute and IP: the admin signs in once (setup) and specs reuse that session.
  projects: [
    { name: 'setup', testMatch: /.*\.setup\.ts/ },
    { name: 'chromium', use: { ...devices['Desktop Chrome'] }, dependencies: ['setup'] },
  ],
})
