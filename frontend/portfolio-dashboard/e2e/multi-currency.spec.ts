import { expect, test, type Page } from '@playwright/test'
import { ADMIN_STATE } from './helpers'

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

  // 1. A portfolio in EUR (previously only the default currency was allowed).
  await page.goto('/settings')
  await page.getByLabel('Name').fill(name)
  await page.getByRole('combobox', { name: 'Portfolio currency' }).click()
  await page.getByRole('option', { name: 'EUR', exact: true }).click()
  await expect(page.getByText('Investment (EUR)')).toBeVisible()
  await page.getByRole('button', { name: 'Create' }).click()
  await expect(page.getByText(`Portfolio “${name}” created`)).toBeVisible()

  // 2. Stocks from four markets; the tray explains the conversion instead of refusing the mix.
  await page.goto('/stocks')
  await page.getByRole('button', { name: 'All markets' }).click()
  await pick(page, 'AAPL', 'AAPL')
  await pick(page, 'SAP.DE', 'SAP.DE')
  await pick(page, '7203.T', '7203.T')
  await pick(page, 'AZN.L', 'AZN.L')
  const tray = page.getByRole('region', { name: 'Selected stocks' })
  await expect(tray.getByLabel('Currency conversion')).toContainText(/trades in .*EUR.*GBP.*JPY.*USD/)
  await expect(tray.getByLabel('Currency conversion')).toContainText('computed in EUR')
  // Stocks without stored history are downloaded first (on a fresh stack most non-US listings have none yet).
  const fetch = tray.getByRole('button', { name: /Fetch prices/ })
  for (let round = 0; round < 3 && await fetch.waitFor({ timeout: 10_000 }).then(() => true, () => false); round++) {
    await expect(fetch).toBeEnabled({ timeout: 15_000 })
    await fetch.click()
    // The button label switches to "Downloading history…" while the job runs; the toast reports completion.
    await expect(page.getByText('Price history downloaded').first()).toBeVisible({ timeout: 120_000 })
  }
  await expect(fetch).toBeHidden({ timeout: 15_000 })

  // 3. Optimization in EUR with converted returns, native currency per stock and the currency exposure.
  await tray.getByRole('link', { name: 'Optimize' }).click()
  await page.getByRole('combobox', { name: 'Optimization method' }).click()
  await page.getByRole('option', { name: 'Minimum variance' }).click()
  await page.getByRole('button', { name: 'Run optimization' }).click()
  await expect(page.getByText(/^In EUR · FX MockFx$/)).toBeVisible({ timeout: 60_000 })
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
  await page.goto('/holdings')
  await expect(page.getByRole('columnheader', { name: /Market value \(EUR\)/ })).toBeVisible()
  const toyota = page.getByRole('row', { name: /7203\.T/ })
  await expect(toyota).toContainText('¥')
  await expect(toyota).toContainText(/1 JPY = 0\.\d+ EUR/)
  await expect(page.getByRole('list', { name: 'Currency exposure' })).toBeVisible()
  const totalEur = await page.getByText('Total value', { exact: true }).first().locator('xpath=..').innerText()

  await page.getByRole('combobox', { name: 'Show values in currency' }).click()
  await page.getByRole('option', { name: 'USD', exact: true }).click()
  await expect(page.getByRole('columnheader', { name: /Market value \(USD\)/ })).toBeVisible()
  await expect(page.getByText('Shown in USD – a reporting conversion')).toBeVisible()
  await expect(page.getByText(/Nothing is exchanged and the holdings do not change/)).toBeVisible()
  await expect(toyota).toContainText(/1 JPY = 0\.\d+ USD/)

  await page.getByRole('combobox', { name: 'Show values in currency' }).click()
  await page.getByRole('option', { name: 'EUR (portfolio)' }).click()
  await expect(page.getByRole('columnheader', { name: /Market value \(EUR\)/ })).toBeVisible()
  expect(await page.getByText('Total value', { exact: true }).first().locator('xpath=..').innerText()).toBe(totalEur)
})
