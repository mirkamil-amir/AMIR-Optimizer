import { expect, type Page } from '@playwright/test'

export const admin = {
  email: process.env.E2E_ADMIN_EMAIL ?? '',
  password: process.env.E2E_ADMIN_PASSWORD ?? '',
}

export function requireAdminCredentials() {
  if (!admin.email || !admin.password) throw new Error('Set E2E_ADMIN_EMAIL and E2E_ADMIN_PASSWORD to the bootstrap administrator.')
}

export const uniqueEmail = (prefix: string) => `${prefix}-${Date.now()}-${Math.floor(Math.random() * 1e6)}@e2e.test`
export const strongPassword = 'E2e-Password-2026'

/** Submits the sign-in form and waits for the API's answer (and, on success, for the app to leave the sign-in page). */
export async function signIn(page: Page, email: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('E-mail').fill(email)
  await page.getByLabel('Password').fill(password)
  const [response] = await Promise.all([
    page.waitForResponse((r) => r.url().endsWith('/api/v1/auth/login')),
    page.getByRole('button', { name: 'Sign in' }).click(),
  ])
  if (response.ok()) await expect(page).not.toHaveURL(/\/login/)
  return response
}

/** Signs out via a sign-out control and waits for the full page reload that discards in-memory state. */
export async function signOutWith(page: Page, click: () => Promise<void>) {
  const reloaded = page.waitForEvent('load')
  await click()
  await reloaded
  await expect(page).toHaveURL(/\/login$/)
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
}

export async function signOut(page: Page) {
  await page.getByRole('button', { name: 'Account menu' }).click()
  await signOutWith(page, () => page.getByRole('menuitem', { name: 'Sign out' }).click())
}

export async function register(page: Page, name: string, email: string, password = strongPassword) {
  await page.goto('/register')
  await page.getByLabel('Name').fill(name)
  await page.getByLabel('E-mail').fill(email)
  await page.getByLabel('Password', { exact: true }).fill(password)
  await page.getByLabel('Confirm password').fill(password)
  await page.getByRole('button', { name: 'Submit application' }).click()
  await expect(page.getByText('Application received')).toBeVisible()
}

/** Approves a pending user through the admin UI (admin must be signed in). */
export async function approve(page: Page, email: string) {
  await page.goto('/admin/users')
  // Accounts under the reserved .test domain are hidden by default; e2e applicants use it.
  await page.getByRole('switch', { name: 'Show test accounts' }).click()
  await page.getByLabel('Search users').fill(email)
  const row = page.getByRole('row').filter({ hasText: email })
  await row.getByRole('button', { name: 'Approve' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Confirm' }).click()
  await expect(page.getByRole('alertdialog')).toBeHidden()
}

/** Session of the administrator saved by admin.setup.ts (gitignored). */
export const ADMIN_STATE = 'playwright/.auth/admin.json'
