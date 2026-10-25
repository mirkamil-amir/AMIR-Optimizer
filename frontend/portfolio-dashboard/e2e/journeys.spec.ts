import { expect, test } from '@playwright/test'
import { ADMIN_STATE, acceptDownloadIfAsked } from './helpers'

// Main student and administrator journeys on the deterministic Mock data of the e2e stack.
test.use({ storageState: ADMIN_STATE })

test('header shows the signed-in name; the role is only in its label', async ({ page }) => {
  await page.goto('/')
  const identity = page.getByLabel(/^Signed in as .*, Admin Investor$/)
  await expect(identity).toContainText('Administrator')
  await expect(identity).not.toContainText('Admin Investor')
})

test('stock selection loads prices automatically and refreshes a single quote', async ({ page }) => {
  await page.goto('/stocks')
  await page.getByLabel('Search company name or ticker').fill('Apple')
  const row = page.getByRole('row', { name: /AAPL/ })
  // A price (never "live") with its kind and date, loaded without any manual refresh.
  await expect(row).toContainText(/\$\d/)
  await expect(row).toContainText(/Last close|Delayed quote/)
  await expect(row).not.toContainText('No price available')

  await row.getByRole('button', { name: 'Refresh price of AAPL' }).click()
  await expect(page.getByText(/AAPL: price (updated|is already current)/)).toBeVisible()
})

test('markets and indices organise the universe, with prices in local currency', async ({ page }) => {
  await page.goto('/stocks')
  await page.getByRole('button', { name: /Germany/ }).click()
  await page.getByRole('button', { name: /^DAX 40/ }).click()
  await expect(page.getByText(/^40 stock\(s\) · DAX 40/)).toBeVisible()
  await expect(page.getByRole('row', { name: /SAP\.DE/ })).toContainText('€')
  await expect(page.getByRole('button', { name: /DAX 30 \(historical\)/ })).toBeVisible()

  await page.getByRole('button', { name: /Japan/ }).click()
  await page.getByLabel('Search company name or ticker').fill('Toyota')
  await expect(page.getByRole('row', { name: /7203\.T/ })).toContainText('¥')
})

test('optimizing a selection explains the actual result', async ({ page }) => {
  await page.goto('/stocks')
  const search = page.getByLabel('Search company name or ticker')
  for (const symbol of ['AAPL', 'MSFT', 'JPM', 'XOM']) {
    await search.fill(symbol)
    await page.getByRole('checkbox', { name: `Select ${symbol}` }).check()
  }
  await page.getByRole('region', { name: 'Selected stocks' }).getByRole('link', { name: /^Math Optimization in “/ }).click()
  // Minimum variance is always feasible (maximum Sharpe is undefined when no stock beats the risk-free rate).
  await page.getByRole('combobox', { name: 'Optimization method' }).click()
  await page.getByRole('option', { name: 'Minimum variance' }).click()
  await page.getByRole('button', { name: 'Run optimization' }).click()
  await acceptDownloadIfAsked(page)
  const insights = page.getByText('What your result means')
  await expect(insights).toBeVisible({ timeout: 30_000 })
  // The interpretation is folded at the bottom of the page until opened.
  await page.getByRole('button', { name: /^What your result means/ }).click()
  await expect(page.getByText(/The portfolio holds \d+ of \d+ eligible stocks/)).toBeVisible()
  await expect(page.getByText(/Minimum variance only looks at risk/)).toBeVisible()
  await expect(page.getByText(/SIMULATED prices/).first()).toBeVisible()

  // Journey F: the reality check challenges the saved result with labelled findings and sensitivity scenarios.
  await page.getByRole('link', { name: 'Portfolio reality check' }).click()
  await expect(page.getByRole('heading', { name: 'What the analysis shows' })).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText('Caution').first()).toBeVisible()
  await expect(page.getByText(/use the same data that chose the weights/)).toBeVisible()
  await expect(page.getByRole('table', { name: 'Sensitivity scenarios' })).toContainText('Equal weight (reference)')
  await expect(page.getByText(/not a recommendation to buy or sell/)).toBeVisible()
  await page.getByRole('link', { name: 'Optimization result' }).click()

  await page.getByRole('link', { name: /How this strategy works/ }).click()
  await expect(page).toHaveURL(/\/learn\//)
})

test('strategy guide has the 12-part structure and a glossary', async ({ page }) => {
  await page.goto('/learn/maximum-sharpe')
  await expect(page.getByRole('heading', { name: 'Maximum-Sharpe (tangency) portfolio' })).toBeVisible()
  for (const title of ['What is this strategy?', 'The mathematics', 'Worked example', 'Important caveats'])
    await expect(page.locator('summary', { hasText: title })).toBeVisible()
  await expect(page.locator('details')).toHaveCount(12)
  await page.goto('/learn/glossary')
  await page.getByLabel('Search glossary').fill('survivorship')
  await expect(page.getByText('Survivorship bias')).toBeVisible()
})

test('administrator overview shows market-data health without secrets', async ({ page }) => {
  await page.goto('/admin')
  await expect(page.getByRole('heading', { name: 'Admin overview' })).toBeVisible()
  await expect(page.getByRole('table', { name: 'Market universes' })).toContainText('DAX 40')
  await expect(page.getByText('Simulated data', { exact: true }).first()).toBeVisible()
  await expect(page.locator('body')).not.toContainText(/apikey|password=/i)
})
