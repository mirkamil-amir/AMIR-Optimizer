import { expect, test } from '@playwright/test'
import { ADMIN_STATE } from './helpers'

// Stock-detail panel opened from the stock list (e2e stack: simulated prices and simulated company profiles).
test.use({ storageState: ADMIN_STATE })

test('detail panel: large layout, Previous/Next/Close, ranges, description and selection kept', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/stocks')
  await page.getByRole('button', { name: /Germany/ }).click()
  await page.getByRole('button', { name: /^DAX 40/ }).click()
  await expect(page.getByText(/^40 stock\(s\) · DAX 40/)).toBeVisible()

  // A selection made before opening the panel must survive it.
  await page.getByRole('checkbox', { name: 'Select SAP.DE' }).check()

  const rows = page.getByRole('row').filter({ has: page.getByRole('checkbox', { name: /^Select / }) })
  const first = (await rows.first().getByRole('checkbox').getAttribute('aria-label'))!.replace('Select ', '')
  const second = (await rows.nth(1).getByRole('checkbox').getAttribute('aria-label'))!.replace('Select ', '')
  await rows.first().getByText(first, { exact: true }).click()

  const dialog = page.getByRole('dialog')
  await expect(dialog.getByRole('heading', { name: new RegExp(`· ${first.replace('.', '\\.')}$`) })).toBeVisible()
  await expect.poll(async () => (await dialog.boundingBox())!.width / 1440).toBeGreaterThan(0.6)
  const box = (await dialog.boundingBox())!
  expect(box.width / 1440).toBeGreaterThan(0.6)
  expect(box.width / 1440).toBeLessThan(0.75)
  expect(box.height / 900).toBeGreaterThan(0.8)
  expect(box.height / 900).toBeLessThanOrEqual(0.9)
  await expect(dialog.getByText(/^1 of 40 in the current list$/)).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Previous' })).toBeDisabled()
  await expect(dialog.getByText(/SIMULATED profile of /)).toBeVisible()
  // DAX stocks have no stored history on a fresh stack: the panel says so and offers the download.
  const download = dialog.getByRole('button', { name: 'Download price history' })
  if (await download.isVisible()) {
    await download.click()
    await expect(page.getByText(new RegExp(`Price history for ${first.replace('.', '\\.')} downloaded`))).toBeVisible({ timeout: 60_000 })
  }
  await expect(dialog.getByText(/Change 1Y:/)).toBeVisible()
  await dialog.getByRole('radiogroup', { name: 'Time range' }).getByRole('radio', { name: '3M' }).click()
  await expect(dialog.getByText(/Change 3M:/)).toBeVisible()

  await dialog.getByRole('button', { name: 'Next' }).click()
  await expect(dialog.getByRole('heading', { name: new RegExp(`· ${second.replace('.', '\\.')}$`) })).toBeVisible()
  await expect(dialog.getByText(/^2 of 40 in the current list$/)).toBeVisible()
  await expect(dialog.getByText(new RegExp(`SIMULATED profile of `))).toBeVisible()

  await dialog.getByRole('button', { name: 'Previous' }).click()
  await expect(dialog.getByRole('heading', { name: new RegExp(`· ${first.replace('.', '\\.')}$`) })).toBeVisible()
  await expect(dialog.getByText(/Change 3M:/)).toBeVisible() // range kept while navigating

  await dialog.getByRole('button', { name: 'Close' }).click()
  await expect(dialog).toBeHidden()
  await expect(page.getByText(/^40 stock\(s\) · DAX 40/)).toBeVisible()
  await expect(page.getByRole('checkbox', { name: 'Select SAP.DE' })).toBeChecked()

  // Escape also closes.
  await rows.nth(1).getByText(second, { exact: true }).click()
  await expect(dialog).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(dialog).toBeHidden()
})

test('detail panel: explicit fallback when no description exists, full-screen on a phone', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  await page.goto('/stocks')
  // PODD (Insulet) has no article link in the pinned S&P 500 table.
  await page.getByLabel('Search company name or ticker').fill('PODD')
  await page.getByRole('row', { name: /PODD/ }).getByText('PODD', { exact: true }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText('Company description is currently unavailable.')).toBeVisible()
  // Full screen once the opening animation has finished.
  await expect.poll(async () => (await dialog.boundingBox())!.width).toBeGreaterThanOrEqual(388)
  await expect(dialog.getByRole('button', { name: 'Next' })).toBeVisible()
  await expect(dialog.getByRole('button', { name: 'Close' })).toBeVisible()
  await dialog.getByRole('button', { name: 'Close' }).click()
  await expect(dialog).toBeHidden()
})
