import { expect, test } from '@playwright/test'
import { admin, requireAdminCredentials, signIn } from './helpers'

test.beforeAll(requireAdminCredentials)

// The globe (D-56): the sign-in page is the globe screen; signed-in users get the Enter intro once per session.
// Automated browsers skip that intro unless `?intro=1`; once-per-session and the Settings switch are covered by Vitest.
test('sign in on the globe, then the Enter intro plays until Enter', async ({ page }) => {
  await page.goto('/login')
  await expect(page.getByTestId('sign-in-globe')).toBeVisible()
  await expect(page.getByTestId('globe-canvas')).toBeVisible()
  await expect(page.getByRole('heading', { name: 'Sign in' })).toBeVisible()
  await expect(page.getByLabel('E-mail')).toBeFocused()
  await expect(page.getByRole('link', { name: 'Apply for access' })).toBeVisible()

  const response = await signIn(page, admin.email, admin.password)
  expect(response.ok()).toBe(true)
  await expect(page.getByTestId('sign-in-globe')).toHaveCount(0)
  await expect(page.getByTestId('globe-intro')).toHaveCount(0) // signed in on the globe: no second globe

  await page.goto('/?intro=1')
  const intro = page.getByRole('dialog', { name: 'Matrix is starting' })
  await expect(intro).toBeVisible()
  await expect(page.getByRole('button', { name: 'Enter' })).toBeFocused()
  await page.waitForTimeout(3000) // no time limit
  await expect(intro).toBeVisible()
  await page.keyboard.press('Enter')
  await expect(intro).toHaveCount(0)
})
