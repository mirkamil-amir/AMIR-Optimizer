import { expect, test } from '@playwright/test'
import { ADMIN_STATE } from './helpers'

test.use({ storageState: ADMIN_STATE })

test('missing prices and rates: the experiment asks, cancel starts nothing, download and continue starts it', async ({ page }) => {
  await page.goto('/stocks')
  const search = page.getByLabel('Search company name or ticker')
  for (const symbol of ['AAPL', 'MSFT', 'NVDA', 'JPM', 'SAP.DE']) {
    await search.fill(symbol)
    await page.getByRole('checkbox', { name: `Select ${symbol}` }).check()
  }
  // Client-side navigation keeps the in-memory selection.
  await page.getByRole('navigation', { name: 'Viewer sections' }).first().getByRole('link', { name: 'ML', exact: true }).click()
  await expect(page.getByRole('heading', { name: 'ML Optimization', exact: true })).toBeVisible()
  await page.getByRole('button', { name: /^Use stock selection/ }).click()  // like Math Optimization: the portfolio's universe unless replaced
  await page.getByLabel('History (years)').fill('15')                      // older than anything stored

  const start = page.getByRole('button', { name: 'Run ML optimization' })
  await start.click()
  const dialog = page.getByRole('dialog', { name: 'Data missing for this analysis' })
  await expect(dialog).toBeVisible()
  await expect(dialog.getByText('Prices (')).toBeVisible()
  await expect(dialog.getByText(/the analysis needs them from/).first()).toBeVisible()
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/missing-data-dialog.png` })

  await dialog.getByRole('button', { name: 'Cancel' }).click()
  await expect(dialog).toBeHidden()
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\/ml-optimize$/)  // nothing was started

  await start.click()
  await expect(dialog).toBeVisible()
  await dialog.getByRole('button', { name: 'Download and continue' }).click()
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\/ml-optimize\/[0-9a-f-]{36}$/, { timeout: 180_000 })
})
