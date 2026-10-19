import { expect, test } from '@playwright/test'
import { ADMIN_STATE } from './helpers'

// Currency converter on the overview and stock-selection pages, the AMIR brand, and switchable themes.
test.use({ storageState: ADMIN_STATE })

test('currency converter (overview only) shows dated reference rates, a chart and a calculator', async ({ page }) => {
  await page.goto('/stocks')
  await expect(page.getByRole('heading', { name: 'Portfolio Creation' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Currency converter' })).toHaveCount(0)
  for (const path of ['/']) {
    await page.goto(path)
    const card = page.getByRole('region', { name: 'Currency converter' })
    await expect(card).toBeVisible()
    await expect(card.getByText(/SIMULATED/).first()).toBeVisible()             // e2e stack: simulated rates, labelled
    await expect(card.getByRole('list', { name: /Exchange rates against/ })).toBeVisible()
    await expect(card.getByLabel('Converted amount')).toHaveText(/\d/)
    await expect(card.getByText(/not live/)).toBeVisible()
  }
  const card = page.getByRole('region', { name: 'Currency converter' })
  const before = await card.getByLabel('Converted amount').innerText()
  await card.getByLabel('Amount to convert').fill('2000')
  await expect(card.getByLabel('Converted amount')).not.toHaveText(before)
  await card.getByRole('radio', { name: '3M' }).click()
  await expect(card.getByText(/3M change:/)).toBeVisible()
  // The base of the board ("1 USD =") is selectable; the list and the chart follow it.
  await card.getByRole('combobox', { name: 'Base currency of the rates' }).click()
  await page.getByRole('option', { name: /^JPY/ }).click()
  await expect(card.getByRole('list', { name: 'Exchange rates against JPY' })).toBeVisible()
  await expect(card.getByText(/per JPY/)).toBeVisible()
  // Turkish lira is on the board by default; any other supported currency can be added, charted and converted.
  const list = card.getByRole('list', { name: 'Exchange rates against JPY' })
  await expect(list.getByRole('button', { name: /^TRY/ })).toHaveText(/\d/)
  await card.getByRole('combobox', { name: 'Add a currency to the rates' }).click()
  await page.getByRole('option', { name: /^ZAR.*South African Rand/ }).click()
  await expect(list.getByRole('button', { name: /^ZAR/ })).toHaveText(/\d/)
  await expect(card.getByRole('combobox', { name: 'Currency shown in the chart' })).toHaveText(/ZAR/)
  await card.getByRole('combobox', { name: 'Convert from' }).click()
  await page.getByRole('option', { name: /^TRY.*Turkish Lira/ }).click()
  await expect(card.getByLabel('Converted amount')).toHaveText(/\d/)
  await expect(card.getByText(/1 TRY = /)).toBeVisible()
})

test('AMIR brand and themes: logo in the shell, dark mode and skins persist', async ({ page }) => {
  await page.goto('/')
  await expect(page).toHaveTitle(/AMIR Optimizer – Allocation · Modelling · Investment · Research/)
  await expect(page.getByRole('link', { name: 'AMIR Optimizer – home' })).toBeVisible()
  await expect(page.locator('[title*="Allocation · Modelling · Investment · Research"]').first()).toBeAttached()

  await page.goto('/settings#appearance')
  await page.getByRole('radio', { name: /Dark/ }).click()
  await page.getByRole('radio', { name: 'Burgundy' }).click()
  await expect(page.locator('html')).toHaveClass(/dark/)
  await expect(page.locator('html')).toHaveAttribute('data-skin', 'burgundy')
  await page.reload()
  await expect(page.locator('html')).toHaveClass(/dark/)                        // applied before React renders
  await expect(page.locator('html')).toHaveAttribute('data-skin', 'burgundy')
  await page.getByRole('radio', { name: /Light/ }).click()
  await page.getByRole('radio', { name: 'Navy & Gold' }).click()
  await expect(page.locator('html')).not.toHaveClass(/dark/)
})
