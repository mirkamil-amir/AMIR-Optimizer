import { expect, test, type Page } from '@playwright/test'
import { admin, approve, register, requireAdminCredentials, signIn, signOut, strongPassword, uniqueEmail } from './helpers'

// Journeys D (watchlist) and E (financial news) for a newly approved student, on the e2e stack's simulated data.
test.beforeAll(requireAdminCredentials)

async function newStudent(page: Page) {
  const email = uniqueEmail('watcher')
  await register(page, 'E2E Watcher', email)
  await signIn(page, admin.email, admin.password)
  await approve(page, email)
  await signOut(page)
  await signIn(page, email, strongPassword)
  return email
}

test('watchlist: add, inspect history and news, persist across sign-in, remove', async ({ page }) => {
  test.setTimeout(120_000)
  const email = await newStudent(page)

  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Watchlist' }).click()
  await expect(page.getByText('Your watchlist is empty')).toBeVisible()
  await page.getByLabel('Search stocks to add').fill('Apple')
  await page.getByRole('listitem').filter({ hasText: 'AAPL' }).getByRole('button', { name: 'Add' }).click()
  await expect(page.getByText('AAPL added to your watchlist')).toBeVisible()
  const row = page.getByRole('link', { name: 'AAPL', exact: true })
  await expect(row).toBeVisible()
  await expect(page.getByText(/of 100 slots used/)).toBeVisible()

  // Period change follows the selected range.
  await page.getByRole('radiogroup', { name: 'Change period' }).getByRole('radio', { name: '1Y' }).click()
  await expect(page.getByRole('img', { name: /AAPL .* over 1Y/ })).toBeVisible()

  // Security page: price with provenance, history ranges (1D honestly unsupported), company news and event comparison.
  await row.click()
  await expect(page).toHaveURL(/\/stocks\/AAPL$/)
  await expect(page.getByText(/SIMULATED/).first()).toBeVisible()
  const ranges = page.getByRole('radiogroup', { name: 'Time range' })
  await ranges.getByRole('radio', { name: '3M' }).click()
  await expect(page.getByText(/Change 3M:/)).toBeVisible()
  await ranges.getByRole('radio', { name: '1D' }).click()
  await expect(page.getByText(/Intraday prices are not stored/)).toBeVisible()

  await expect(page.getByRole('heading', { name: 'Company news' })).toBeVisible()
  const story = page.getByRole('article', { name: 'SIMULATED: Apple Inc. reports quarterly earnings' })
  await expect(story).toBeVisible()
  await expect(story.getByText(/also reported by Simulated Daily/)).toBeVisible()
  await story.getByRole('button', { name: 'Compare AAPL price movement around this article' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog.getByText(/Event and price-movement comparison · AAPL/)).toBeVisible()
  await expect(dialog.getByText(/Reference close \(before publication\)/)).toBeVisible()
  await expect(dialog.getByText(/not evidence that the article caused the move/)).toBeVisible()
  await page.keyboard.press('Escape')

  // Persistence across sign-out/sign-in.
  await signOut(page)
  await signIn(page, email, strongPassword)
  await page.goto('/watchlist')
  await expect(page.getByRole('link', { name: 'AAPL', exact: true })).toBeVisible()

  await page.getByRole('button', { name: 'Remove AAPL from watchlist' }).click()
  await expect(page.getByText('Your watchlist is empty')).toBeVisible()
})

test('watchlist: a row opens the stock; previous/next step through the watchlist', async ({ page }) => {
  test.setTimeout(120_000)
  await newStudent(page)
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Watchlist' }).click()
  for (const [term, symbol] of [['Apple', 'AAPL'], ['Microsoft', 'MSFT']]) {
    await page.getByLabel('Search stocks to add').fill(term)
    await page.getByRole('listitem').filter({ hasText: symbol }).getByRole('button', { name: 'Add' }).click()
    await expect(page.getByText(`${symbol} added to your watchlist`)).toBeVisible()
  }
  await expect(page.getByRole('button', { name: 'Details', exact: true })).toHaveCount(0)
  const rows = page.locator('[data-testid^="watch-row-"]')
  await expect(rows).toHaveCount(2)
  const order = await rows.evaluateAll((els) => els.map((r) => r.getAttribute('data-testid')!.replace('watch-row-', '')))
  const [first, second] = order

  // The row's own controls keep their function and do not open the page.
  await page.getByRole('button', { name: `Refresh price of ${first}` }).click()
  await expect(page).toHaveURL(/\/watchlist$/)

  // A click on the row itself (its exchange/currency line) opens the stock page.
  await page.getByTestId(`watch-row-${first}`).getByText(/ · USD$/).first().click()
  await expect(page).toHaveURL(new RegExp(`/stocks/${first}$`))
  const nav = page.getByRole('navigation', { name: 'Watchlist navigation' })
  await expect(nav.getByText('1 of 2')).toBeVisible()
  await expect(nav.getByRole('button', { name: 'Previous stock' })).toBeDisabled()

  await nav.getByRole('link', { name: `Next stock: ${second}` }).click()
  await expect(page).toHaveURL(new RegExp(`/stocks/${second}$`))
  await expect(page.getByRole('heading', { name: new RegExp(`^${second} ·`) })).toBeVisible()
  await expect(nav.getByText('2 of 2')).toBeVisible()
  await expect(nav.getByRole('button', { name: 'Next stock' })).toBeDisabled()

  await nav.getByRole('link', { name: `Previous stock: ${first}` }).click()
  await expect(page).toHaveURL(new RegExp(`/stocks/${first}$`))
})

test('financial news: views, filters, official sources and attribution', async ({ page }) => {
  await newStudent(page)
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'Financial News' }).click()
  await expect(page.getByRole('heading', { name: 'Financial news' })).toBeVisible()

  await page.getByRole('tab', { name: 'Macroeconomic' }).click()
  const macro = page.getByText('SIMULATED: Central bank leaves interest rates unchanged')
  await expect(macro).toBeVisible()
  await expect(page.getByText('Official source').first()).toBeVisible()
  await expect(page.getByText(/Potential relevance: (Low|Medium|High)/).first()).toBeVisible()

  // Original article opens in a new tab with safe rel attributes.
  const link = page.getByRole('link', { name: /SIMULATED: Central bank leaves interest rates unchanged/ })
  await expect(link).toHaveAttribute('target', '_blank')
  await expect(link).toHaveAttribute('rel', /noopener/)

  // Watchlist view is empty until a stock is watched.
  await page.getByRole('tab', { name: 'My watchlist' }).click()
  await expect(page.getByText('No news about your watched stocks yet')).toBeVisible()

  // Attribution and limitations are disclosed; the news-source table (provider names, fetch status) is for administrators.
  await page.getByText('Attribution and limitations').click()
  await expect(page.getByText(/Articles are linked to stocks only when the provider tagged them/)).toBeVisible()
  await expect(page.getByRole('columnheader', { name: 'Last fetched' })).toHaveCount(0)
})
