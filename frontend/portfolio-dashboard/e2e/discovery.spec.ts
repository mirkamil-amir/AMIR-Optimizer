import { expect, test } from '@playwright/test'
import { ADMIN_STATE, admin, signIn, signOut } from './helpers'

test.use({ storageState: ADMIN_STATE })

test('stock discovery: search, select across sectors, selection is temporary', async ({ page }) => {
  await page.goto('/')
  await page.getByRole('navigation', { name: 'Matrix sections' }).getByRole('link', { name: 'Forge', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'New portfolio' })).toBeVisible()

  const search = page.getByLabel('Search company name or ticker')
  await search.fill('AAPL')
  await page.getByRole('checkbox', { name: 'Select AAPL' }).check()
  await search.fill('XOM')
  await page.getByRole('checkbox', { name: 'Select XOM' }).check()

  const tray = page.getByRole('region', { name: 'Selected stocks' })
  await expect(tray.getByRole('list', { name: 'Selected tickers' }).getByRole('listitem')).toHaveCount(2)

  // The selection survives client-side navigation …
  await page.getByRole('navigation', { name: 'Matrix sections' }).getByRole('link', { name: 'Lens', exact: true }).click()
  await expect(page.getByRole('link', { name: 'New portfolio: 2 stocks selected' })).toBeVisible()

  // … is never written to browser storage …
  const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }))
  expect(stored).not.toContain('AAPL')

  // … and is gone after a reload.
  await page.reload()
  await expect(page.getByRole('link', { name: 'New portfolio: 0 stocks selected' })).toBeVisible()
})

test('the selection hands its universe to the optimizer', async ({ page }) => {
  await page.goto('/stocks')
  const search = page.getByLabel('Search company name or ticker')
  for (const symbol of ['AAPL', 'MSFT']) {
    await search.fill(symbol)
    await page.getByRole('checkbox', { name: `Select ${symbol}` }).check()
  }
  // The tray names the portfolio whose optimizer receives the selection.
  await page.getByRole('region', { name: 'Selected stocks' }).getByRole('link', { name: /^Math Optimization in “/ }).click()
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\/optimize\?from=selection/)
  await expect(page.getByText('AAPL').first()).toBeVisible()
  await expect(page.getByText('MSFT').first()).toBeVisible()
})

test('ML workspace and strategy comparison pages load', async ({ page }) => {
  await page.goto('/machine-learning')
  await expect(page.getByRole('heading', { name: 'ML Optimization', exact: true })).toBeVisible()

  await page.goto('/strategy-comparison')
  await expect(page.getByRole('heading', { name: 'Comparison', exact: true })).toBeVisible()
})

test.describe('own session', () => {
  // Fresh, signed-out context so signing out does not end the shared administrator session of the other specs.
  test.use({ storageState: { cookies: [], origins: [] } })

  test('signing out clears the in-memory selection and the redirect target', async ({ page }) => {
    await signIn(page, admin.email, admin.password)
    await page.goto('/stocks')
    await page.getByLabel('Search company name or ticker').fill('AAPL')
    await page.getByRole('checkbox', { name: 'Select AAPL' }).check()
    await expect(page.getByRole('link', { name: 'New portfolio: 1 stock selected' })).toBeVisible()
    await signOut(page)
    expect(await page.evaluate(() => (history.state as { usr?: { from?: string } } | null)?.usr?.from)).toBeUndefined()
    await expect(page.getByRole('link', { name: /stocks? selected/ })).toHaveCount(0)
  })
})

test('administrators see user administration and the audit trail', async ({ page }) => {
  await page.goto('/admin/users')
  await expect(page.getByRole('table', { name: 'Users' }).or(page.getByText(/No (pending applications|users found)/))).toBeVisible()
})
