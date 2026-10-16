import { expect, test } from '@playwright/test'
import { ADMIN_STATE } from './helpers'

// Currency converter on the overview and stock-selection pages, the AMIR brand, and switchable themes.
test.use({ storageState: ADMIN_STATE })

test('currency converter shows dated reference rates, a chart and a calculator', async ({ page }) => {
  for (const path of ['/', '/stocks']) {
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
})

test('AMIR brand and themes: logo in the shell, dark mode and skins persist', async ({ page }) => {
  await page.goto('/')
  await expect(page).toHaveTitle(/AMIR Optimizer – Allocation · Modelling · Investment · Research/)
  await expect(page.getByRole('link', { name: 'AMIR Optimizer – home' })).toBeVisible()
  await expect(page.getByText('Allocation · Modelling · Investment · Research').first()).toBeVisible()

  await page.goto('/settings#appearance')
  await page.getByRole('radio', { name: /Dark/ }).click()
  await page.getByRole('radio', { name: /Ocean/ }).click()
  await expect(page.locator('html')).toHaveClass(/dark/)
  await expect(page.locator('html')).toHaveAttribute('data-skin', 'ocean')
  await page.reload()
  await expect(page.locator('html')).toHaveClass(/dark/)                        // applied before React renders
  await expect(page.locator('html')).toHaveAttribute('data-skin', 'ocean')
  await page.getByRole('radio', { name: /Light/ }).click()
  await page.getByRole('radio', { name: /Graphite/ }).click()
  await expect(page.locator('html')).not.toHaveClass(/dark/)
})
