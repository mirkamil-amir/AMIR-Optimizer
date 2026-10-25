import { expect, test, type Page } from '@playwright/test'
import { ADMIN_STATE, acceptDownloadIfAsked } from './helpers'

// A portfolio in EUR holding stocks from four markets (USD, EUR, JPY, GBP): created, optimized, traded and reported in one
// base currency, on the e2e stack's simulated prices and simulated exchange rates.
test.use({ storageState: ADMIN_STATE })

async function pick(page: Page, query: string, symbol: string) {
  const search = page.getByLabel('Search company name or ticker')
  await search.fill(query)
  await page.getByRole('checkbox', { name: `Select ${symbol}` }).check()
}

test('mixed-currency portfolio: EUR base with US, German, Japanese and UK stocks', async ({ page }) => {
  test.setTimeout(240_000)
  const name = `Mixed EUR ${Date.now()}`

  // 1. Portfolio Creation: a portfolio in EUR with stocks from four markets; the review explains the conversion.
  await page.goto('/stocks')
  await page.getByLabel('Name', { exact: true }).fill(name)
  await page.getByRole('combobox', { name: 'Portfolio currency' }).click()
  await page.getByRole('option', { name: /^EUR/ }).click()
  await expect(page.getByText('Starting cash (EUR)')).toBeVisible()
  await page.getByRole('button', { name: 'All markets' }).click()
  await pick(page, 'AAPL', 'AAPL')
  await pick(page, 'SAP.DE', 'SAP.DE')
  await pick(page, '7203.T', '7203.T')
  await pick(page, 'AZN.L', 'AZN.L')
  const tray = page.getByRole('region', { name: 'Selected stocks' })
  await expect(tray.getByLabel('Currency conversion')).toContainText(/trades in .*EUR.*GBP.*JPY.*USD/)
  await expect(page.getByText(/Stocks in .*GBP.*JPY.*USD are valued in EUR with dated reference rates/)).toBeVisible()
  // Stocks without stored history are downloaded first (on a fresh stack most non-US listings have none yet).
  const fetch = tray.getByRole('button', { name: /Fetch prices/ })
  for (let round = 0; round < 3 && await fetch.waitFor({ timeout: 10_000 }).then(() => true, () => false); round++) {
    // Downloads are fast: the history may arrive (and the button disappear) before the click.
    if (!await fetch.click({ timeout: 15_000 }).then(() => true, () => false)) continue
    // The selection shows the download's progress, then a summary that stays until closed.
    await expect(tray.getByRole('status', { name: /Downloading price history|Download result/ }).or(tray.getByRole('img', { name: /Downloading price history/ })).first()).toBeVisible({ timeout: 30_000 })
    // The button label switches to "Downloading history…" while the job runs; the toast reports completion.
    await expect(page.getByText('Price history downloaded').first()).toBeVisible({ timeout: 120_000 })
  }
  await expect(fetch).toBeHidden({ timeout: 15_000 })

  await page.getByRole('button', { name: 'Create portfolio' }).click()
  await expect(page.getByText(`Portfolio “${name}” created`)).toBeVisible()
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\?created=1$/)
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()

  // 3. Optimization in EUR with converted returns, native currency per stock and the currency exposure.
  await page.getByRole('navigation', { name: 'Portfolio sections' }).getByRole('link', { name: 'Math Optimization' }).click()
  await page.getByRole('combobox', { name: 'Optimization method' }).click()
  await page.getByRole('option', { name: 'Minimum variance' }).click()
  await page.getByRole('button', { name: 'Run optimization' }).click()
  // The fresh e2e database lacks older GBP rates for the lookback: the app asks before running; accept the download.
  await acceptDownloadIfAsked(page)
  await expect(page.getByText(/^In EUR · FX converted$/)).toBeVisible({ timeout: 120_000 })
  const allocation = page.getByRole('table', { name: 'Recommended allocation' })
  await expect(allocation.getByRole('row', { name: /7203\.T/ })).toContainText('JPY')
  await expect(allocation.getByRole('row', { name: /AZN\.L/ })).toContainText('GBP')
  await expect(page.getByRole('list', { name: 'Currency exposure' })).toBeVisible()

  // 4. Proposal: native prices with the rate used; execution is a simulated exchange of the EUR cash.
  await page.getByRole('button', { name: 'Create trade proposal' }).click()
  await expect(page.getByText(/converted to EUR .*SIMULATED currency exchange/)).toBeVisible({ timeout: 30_000 })
  await expect(page.getByText(/1 JPY = 0\.\d+ EUR/).first()).toBeVisible()
  await page.getByRole('button', { name: /Review & execute/ }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Confirm simulated execution' }).click()
  await expect(page.getByRole('alertdialog')).toBeHidden({ timeout: 30_000 })

  // 5. Holdings in EUR: native price, rate and EUR value per position; then a USD reporting view that changes nothing.
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\/holdings$/)
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  await expect(page.getByRole('columnheader', { name: /Market value \(EUR\)/ })).toBeVisible()
  const toyota = page.getByRole('row', { name: /7203\.T/ })
  await expect(toyota).toContainText('¥')
  await expect(toyota).toContainText(/1 JPY = 0\.\d+ EUR/)
  await expect(page.getByRole('list', { name: 'Currency exposure' })).toBeVisible()
  const totalEur = await page.getByText('Total value', { exact: true }).first().locator('xpath=..').innerText()

  await page.getByRole('combobox', { name: 'Show values in currency' }).click()
  await page.getByRole('option', { name: /^USD/ }).click()
  await expect(page.getByRole('columnheader', { name: /Market value \(USD\)/ })).toBeVisible()
  await expect(page.getByText('Shown in USD – a reporting conversion')).toBeVisible()
  await expect(page.getByText(/Nothing is exchanged and the holdings do not change/)).toBeVisible()
  await expect(toyota).toContainText(/1 JPY = 0\.\d+ USD/)

  await page.getByRole('combobox', { name: 'Show values in currency' }).click()
  await page.getByRole('option', { name: /^EUR.*\(portfolio\)/ }).click()
  await expect(page.getByRole('columnheader', { name: /Market value \(EUR\)/ })).toBeVisible()
  expect(await page.getByText('Total value', { exact: true }).first().locator('xpath=..').innerText()).toBe(totalEur)
})
