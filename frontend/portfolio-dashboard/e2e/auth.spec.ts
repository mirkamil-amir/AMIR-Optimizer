import { expect, test } from '@playwright/test'
import { admin, approve, register, requireAdminCredentials, signIn, signOut, signOutWith, strongPassword, uniqueEmail } from './helpers'

test.beforeAll(requireAdminCredentials)

test('anonymous visitors are sent to the sign-in page', async ({ page }) => {
  await page.goto('/stocks')
  await expect(page).toHaveURL(/\/login/)
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
})

test('wrong credentials are rejected with a generic message', async ({ page }) => {
  await signIn(page, uniqueEmail('nobody'), 'Wrong-Password-1')
  await expect(page).toHaveURL(/\/login/)
  await expect(page.getByText('Sign-in failed')).toBeVisible()
})

test('registration requires administrator approval before the app opens', async ({ page }) => {
  const email = uniqueEmail('applicant')
  await register(page, 'E2E Applicant', email)

  // Signing in before approval only shows the status page.
  await signIn(page, email, strongPassword)
  await expect(page).toHaveURL(/\/account/)
  await expect(page.getByText('Waiting for approval')).toBeVisible()
  await page.goto('/stocks')
  await expect(page).toHaveURL(/\/account/)
  await signOutWith(page, () => page.getByRole('button', { name: 'Sign out' }).click())

  // The administrator approves the application.
  await signIn(page, admin.email, admin.password)
  await approve(page, email)
  await signOut(page)

  // Now the user reaches the application (not the page the administrator signed out from) but not the admin area.
  await signIn(page, email, strongPassword)
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByLabel(/^Signed in as .*, Student investor$/)).toBeVisible()
  await page.goto('/admin/users')
  await expect(page.getByText('Administrators only')).toBeVisible()
  await expect(page.getByRole('navigation', { name: 'Administration' })).toHaveCount(0)
  await expect(page.getByRole('link', { name: 'Admin settings' })).toHaveCount(0)

  // Data-source names are for administrators only.
  const providerNames = /\bMock(Fx)?\b|Yahoo|Alpha ?Vantage|\bECB\b/
  for (const path of ['/', '/market-data']) {
    await page.goto(path)
    await expect(page.locator('main')).toBeVisible()
    await page.waitForLoadState('networkidle')
    await expect(page.locator('body')).not.toContainText(providerNames)
  }
  await expect(page.getByText('Providers', { exact: true })).toHaveCount(0)
})

test('a forgotten password is reset by an administrator and replaced at the next sign-in', async ({ page }) => {
  const email = uniqueEmail('forgetful')
  await register(page, 'E2E Forgetful', email)
  await signIn(page, admin.email, admin.password)
  await approve(page, email)

  // The sign-in page explains what to do.
  await signOut(page)
  await page.getByRole('button', { name: 'Forgot your password?' }).click()
  await expect(page.getByText(/Ask an administrator to reset it/)).toBeVisible()

  // The administrator resets it and reads the one-time password, shown once.
  await signIn(page, admin.email, admin.password)
  await page.goto('/admin/users')
  await page.getByRole('tab', { name: 'Approved' }).click()
  await page.getByRole('switch', { name: 'Show test accounts' }).click()
  await page.getByLabel('Search users').fill(email)
  const row = page.getByRole('row').filter({ hasText: email })
  await row.getByRole('button', { name: 'Reset password' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Reset password' }).click()
  const oneTime = (await page.getByTestId('one-time-password').textContent())?.trim() ?? ''
  expect(oneTime).toMatch(/^[A-Za-z2-9]{4}(-[A-Za-z2-9]{4}){3}$/)
  await page.getByRole('alertdialog').getByRole('button', { name: 'Done' }).click()
  await expect(row.getByText('Awaiting new password')).toBeVisible()
  await signOut(page)

  // The old password is gone; the one-time password leads straight to choosing a new one, and nothing else opens first.
  expect((await signIn(page, email, strongPassword)).ok()).toBe(false)
  await signIn(page, email, oneTime)
  await expect(page).toHaveURL(/\/choose-password/)
  await page.goto('/stocks')
  await expect(page).toHaveURL(/\/choose-password/)
  const chosen = 'Chosen-Again-2026'
  await page.getByLabel('One-time password').fill(oneTime)
  await page.getByLabel('New password', { exact: true }).fill(chosen)
  await page.getByLabel('Confirm new password').fill(chosen)
  await page.getByRole('button', { name: 'Save and continue' }).click()
  await expect(page).toHaveURL(/\/$/)
  await expect(page.getByLabel(/^Signed in as .*, Student investor$/)).toBeVisible()

  await signOut(page)
  await signIn(page, email, chosen)
  await expect(page).toHaveURL(/\/$/)
})
