import { expect, test, type Page } from '@playwright/test'
import { ADMIN_STATE } from './helpers'

/** Picks an option of the open currency list; long lists scroll inside the popup, so bring the option into view first. */
async function pick(page: Page, name: RegExp) {
  const option = page.getByRole('option', { name })
  await option.evaluate((e) => e.scrollIntoView({ block: 'nearest' }))
  await option.click()
}

// Currency converter on the overview and stock-selection pages, the MatrixView brand, and switchable themes.
test.use({ storageState: ADMIN_STATE })

test('currency converter (overview only) shows dated reference rates, a chart and a calculator', async ({ page }) => {
  await page.goto('/stocks')
  await expect(page.getByRole('heading', { name: 'New portfolio' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Currency converter' })).toHaveCount(0)
  for (const path of ['/']) {
    await page.goto(path)
    const card = page.getByRole('region', { name: 'Currency converter' })
    await expect(card).toBeVisible()
    await expect(card.getByText(/SIMULATED/).first()).toBeVisible()             // e2e stack: simulated rates, labelled
    await expect(card.getByRole('list', { name: /Exchange rates against/ })).toBeVisible()
    await expect(card.getByLabel('Converted amount')).toHaveText(/\d/)
    await expect(card.getByLabel('Converted amount')).toHaveAttribute('title', /reference rate of/)   // rate and date in the tooltip
    // The rate basis is in the title's tip, not on the card.
    await expect(card.getByText(/not live/)).toHaveCount(0)
    await card.getByRole('button', { name: 'Currency converter' }).hover()
    await expect(page.getByText(/not live/)).toBeVisible()
    await page.mouse.move(0, 0)
  }
  const card = page.getByRole('region', { name: 'Currency converter' })
  const before = await card.getByLabel('Converted amount').innerText()
  await card.getByLabel('Amount to convert').fill('2000')
  await expect(card.getByLabel('Converted amount')).not.toHaveText(before)
  await card.getByRole('radio', { name: '3M' }).click()
  await expect(card.getByText(/3M change:/)).toBeVisible()
  // The list and the chart follow the From box.
  await card.getByRole('combobox', { name: 'Convert from' }).click()
  await pick(page, /^JPY/)
  await expect(card.getByRole('list', { name: 'Exchange rates against JPY' })).toBeVisible()
  await expect(card.getByRole('combobox', { name: 'Base currency of the rates' })).toHaveCount(0)
  // Turkish lira is on the board by default; any other supported currency can be added, charted and converted.
  const list = card.getByRole('list', { name: 'Exchange rates against JPY' })
  await expect(list.getByRole('button', { name: /^TRY/ })).toHaveText(/\d/)
  await card.getByRole('combobox', { name: 'Add a currency to the rates' }).click()
  await pick(page, /^ZAR.*South African Rand/)
  await expect(list.getByRole('button', { name: /^ZAR/ })).toHaveText(/\d/)
  await expect(card.getByRole('combobox', { name: 'Convert to' })).toHaveText(/ZAR/)        // an added currency becomes To
  await expect(card.getByLabel('Converted amount')).toHaveText(/\d/)
  await expect(card.getByLabel('Converted amount')).toHaveAttribute('title', /^1 JPY = /)
  // Rapid adds and removes stay local: rows appear at once and no further rate-board request is made.
  const boards: string[] = []
  page.on('request', (r) => { if (r.url().includes('/fx/board')) boards.push(r.url()) })
  for (const c of ['BRL', 'MXN', 'SGD']) {
    await expect(page.getByRole('listbox')).toHaveCount(0)                   // the previous list has finished closing
    await card.getByRole('combobox', { name: 'Add a currency to the rates' }).click()
    await pick(page, new RegExp(`^${c}`))
    await expect(list.getByRole('button', { name: new RegExp(`^${c}`) })).toHaveText(/\d/)
  }
  await card.getByRole('button', { name: 'Remove MXN' }).click()
  await expect(list.getByRole('button', { name: /^MXN/ })).toHaveCount(0)
  expect(boards).toEqual([])
  // BGN: no longer published (euro since 2026), explained – never a current rate.
  await expect(page.getByRole('listbox')).toHaveCount(0)
  await card.getByRole('button', { name: 'Currency converter' }).hover()
  await expect(page.getByText(/No longer published: BGN/)).toBeVisible()
  await page.mouse.move(0, 0)
})

test('MatrixView brand and themes: logo in the shell, dark mode and skins persist', async ({ page }) => {
  await page.goto('/')
  await expect(page).toHaveTitle('MatrixView')
  await expect(page.getByRole('link', { name: 'MatrixView – home' })).toBeVisible()
  await expect(page.locator('[title="MatrixView"]').first()).toBeAttached()

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
