import { expect, test } from '@playwright/test'

// The start-up globe (D-56). Automated browsers skip it unless `?intro=1` asks for it; once-per-session and the
// Settings switch are covered by Vitest (globe-intro.test.tsx, intro.test.ts).
test('start-up globe: plays over the app, Enter goes in, automation skips it otherwise', async ({ page }) => {
  await page.goto('/login?intro=1')
  const intro = page.getByRole('dialog', { name: 'Matrix is starting' })
  await expect(intro).toBeVisible()
  await expect(page.getByTestId('globe-canvas')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Enter' })).toBeFocused()
  await page.getByRole('button', { name: 'Enter' }).click()
  await expect(intro).toHaveCount(0)
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeEnabled()

  // Without the parameter an automated browser goes straight to the app.
  await page.goto('/login')
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible()
  await expect(page.getByTestId('globe-intro')).toHaveCount(0)

  // It also ends by itself, and Escape skips it.
  await page.goto('/login?intro=1')
  await expect(intro).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(intro).toHaveCount(0)
})
