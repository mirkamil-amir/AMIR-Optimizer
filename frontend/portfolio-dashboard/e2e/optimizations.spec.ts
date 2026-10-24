import { expect, test, type Page } from '@playwright/test'
import { ADMIN_STATE, acceptDownloadIfAsked } from './helpers'

test.use({ storageState: ADMIN_STATE })

const sidebar = (page: Page) => page.getByRole('navigation', { name: 'Optimizer sections' }).first()

async function runOptimization(page: Page, portfolioId: string, method: string) {
  await page.goto(`/portfolios/${portfolioId}/optimize`)
  await expect(page.getByRole('heading', { name: 'Math Optimization' })).toBeVisible()
  await page.getByRole('combobox', { name: 'Optimization method' }).click()
  await page.getByRole('option', { name: method }).click()
  await page.getByRole('button', { name: 'Run optimization' }).click()
  await acceptDownloadIfAsked(page)
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/optimize/[0-9a-f-]{36}$`), { timeout: 120_000 })
  // Only successful runs can be backtested or compared.
  await expect(page.getByText('What your result means')).toBeVisible({ timeout: 30_000 })
}

/** Both optimization pages: every section visible at once (no tabs), the interpretation box last, below the trade proposal. */
async function expectAllSectionsVisible(page: Page, sections: string[], screenshot: string) {
  await expect(page.getByRole('tab')).toHaveCount(0)
  await expect(page.getByRole('button', { name: /^What your result means/ })).toHaveAttribute('aria-expanded', 'false')
  await expect(page.getByRole('button', { name: 'Inputs & methodology' })).toHaveAttribute('aria-expanded', 'false')
  for (const name of sections) await expect(page.getByRole('region', { name })).toBeVisible()
  const proposal = await page.getByText('Trade proposal', { exact: true }).boundingBox()
  const insights = await page.getByText('What your result means', { exact: true }).boundingBox()
  expect(insights!.y).toBeGreaterThan(proposal!.y)
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/${screenshot}`, fullPage: true })
}

test('optimizations of one portfolio: Math and ML pages alike, backtest any optimization, compare math vs math', async ({ page }) => {
  test.setTimeout(300_000)
  // Sidebar: Mathematical and ML Optimization sit together, before the history; testing and comparing follow.
  await page.goto('/')
  await expect(sidebar(page).getByRole('link')).toHaveText(['Overview', 'Portfolio Creation', 'Math Optimization', 'ML Optimization',
    'Optimization history', 'Backtest', 'Strategy comparison'])

  // A portfolio with two mathematical optimizations.
  const name = `Strategies ${Date.now()}`
  await page.getByRole('link', { name: 'Create portfolio' }).first().click()
  await page.getByLabel('Name', { exact: true }).fill(name)
  const search = page.getByLabel('Search company name or ticker')
  for (const s of ['AAPL', 'MSFT', 'NVDA', 'JPM', 'XOM', 'JNJ']) {
    await search.fill(s)
    await page.getByRole('checkbox', { name: `Select ${s}` }).check()
  }
  await page.getByRole('button', { name: 'Create portfolio' }).click()
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\?created=1$/)
  const portfolioId = page.url().match(/portfolios\/([0-9a-f-]{36})/)![1]
  await runOptimization(page, portfolioId, 'Minimum variance')
  await runOptimization(page, portfolioId, 'Equal weight')
  await expectAllSectionsVisible(page, ['Allocation', 'Efficient frontier', 'Correlation', 'Risk contribution', 'Inputs & methodology'], 'math-optimization.png')

  // ML Optimization: a tab of the same portfolio, same layout, the portfolio's universe by default.
  await page.getByRole('navigation', { name: 'Portfolio sections' }).getByRole('link', { name: 'ML Optimization' }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/ml-optimize$`))
  await page.getByLabel('History (years)').fill('5')
  await page.getByRole('button', { name: 'Run ML optimization' }).click()
  await acceptDownloadIfAsked(page)
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/ml-optimize/[0-9a-f-]{36}$`), { timeout: 120_000 })
  await expect(page.getByRole('region', { name: 'Allocation' })).toBeVisible({ timeout: 180_000 })
  await expectAllSectionsVisible(page, ['Allocation', 'Validation & test', 'Walk-forward backtest (test period)', 'Explainability', 'Inputs & methodology'], 'ml-optimization.png')

  // Backtest: a window chooses the portfolio and which of its optimizations is backtested.
  await sidebar(page).getByRole('link', { name: 'Backtest', exact: true }).click()
  await page.getByRole('button', { name: 'Choose optimization…' }).click()
  const window = page.getByRole('dialog', { name: 'Backtest an optimization' })
  await window.getByRole('combobox', { name: 'Portfolio' }).click()
  await page.getByRole('option', { name: new RegExp(`^${name}`) }).click()
  await window.getByRole('combobox', { name: 'Optimization' }).click()
  await expect(page.getByRole('option')).toHaveCount(3)                 // two Math runs and one ML Optimization
  await page.getByRole('option').filter({ hasText: 'Minimum Variance' }).click()
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/backtest-window.png` })
  await window.getByRole('button', { name: 'Run backtest' }).click()
  await acceptDownloadIfAsked(page)
  await expect(page).toHaveURL(/\/backtest\/[0-9a-f-]{36}$/, { timeout: 120_000 })
  await expect(page.getByText(/Math: Minimum Variance/).first()).toBeVisible()
  // Monthly returns as a line chart; the rebalance log folded until opened.
  await expect(page.getByText('Monthly returns', { exact: true })).toBeVisible({ timeout: 120_000 })
  const log = page.getByRole('button', { name: /^Rebalance log/ })
  await expect(log).toHaveAttribute('aria-expanded', 'false')
  await log.click()
  await expect(page.getByRole('table', { name: 'Rebalance log' })).toBeVisible()
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/backtest-result.png`, fullPage: true })

  // Comparison: two mathematical optimizations of the same portfolio (no hybrid – it needs one ML strategy).
  await sidebar(page).getByRole('link', { name: 'Strategy comparison', exact: true }).click()
  await page.getByRole('combobox', { name: 'Portfolio' }).click()
  await page.getByRole('option', { name: new RegExp(`^${name}`) }).click()
  await page.getByRole('combobox', { name: 'Strategy A' }).click()
  await page.getByRole('option').filter({ hasText: 'Minimum Variance' }).click()
  await page.getByRole('combobox', { name: 'Strategy B' }).click()
  await page.getByRole('option').filter({ hasText: 'Equal Weight' }).click()
  await expect(page.getByRole('switch', { name: 'Add hybrid (ML screen + mean-variance)' })).toBeDisabled()
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/comparison-form.png` })
  await page.getByRole('button', { name: 'Compare strategies' }).click()
  await acceptDownloadIfAsked(page)
  await expect(page).toHaveURL(/\/strategy-comparison\/[0-9a-f-]{36}$/, { timeout: 120_000 })
  await expect(page.getByText(/^A: Minimum Variance/).first()).toBeVisible({ timeout: 180_000 })
  await expect(page.getByText(/^B: Equal Weight/).first()).toBeVisible()
  // Data notes are folded away until opened; the interpretation box is the last element of the page.
  for (const title of [/^Universe and data notes/, /^Strategies and methodology/, /^What the comparison shows/]) {
    const toggle = page.getByRole('button', { name: title })
    await expect(toggle).toHaveAttribute('aria-expanded', 'false')
    await toggle.click()
    await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  }
  const notesBox = await page.getByRole('button', { name: /^Universe and data notes/ }).boundingBox()
  const insightsBox = await page.getByRole('button', { name: /^What the comparison shows/ }).boundingBox()
  expect(insightsBox!.y).toBeGreaterThan(notesBox!.y)
  await expect(page.getByText('Monthly returns', { exact: true })).toBeVisible()
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/comparison-result.png`, fullPage: true })

  // The portfolio's own tabs: its backtests, comparisons and runs, without leaving the portfolio.
  const tabs = page.getByRole('navigation', { name: 'Portfolio sections' })
  await page.goto(`/portfolios/${portfolioId}`)
  await tabs.getByRole('link', { name: 'Backtest' }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/backtest$`))
  await expect(page.getByRole('link', { name: /Math: Minimum Variance/ })).toBeVisible()
  await page.getByRole('link', { name: /Math: Minimum Variance/ }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/backtest/[0-9a-f-]{36}$`))
  await tabs.getByRole('link', { name: 'Strategy comparison' }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/comparison$`))
  await expect(page.getByText(/^A: Minimum Variance/).first()).toBeVisible({ timeout: 60_000 })
  await expect(page.getByRole('combobox', { name: 'Portfolio', exact: true })).toHaveCount(0)   // the portfolio is fixed here
  await tabs.getByRole('link', { name: 'Optimization runs' }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/runs$`))
  await expect(page.getByRole('table', { name: 'Math Optimizations of this portfolio' }).getByRole('row')).toHaveCount(3)
  await expect(page.getByRole('table', { name: 'ML Optimizations of this portfolio' }).getByRole('row')).toHaveCount(2)

  // Overview: the former Performance charts, each once; the efficient frontier right below Portfolio vs benchmark.
  await tabs.getByRole('link', { name: 'Overview' }).click()
  await expect(tabs.getByRole('link', { name: 'Performance' })).toHaveCount(0)
  const vsBenchmark = await page.getByText('Portfolio vs benchmark', { exact: true }).boundingBox()
  const frontier = page.getByText('Efficient frontier (latest successful Math Optimization)')
  await expect(frontier).toBeVisible({ timeout: 30_000 })
  expect((await frontier.boundingBox())!.y).toBeGreaterThan(vsBenchmark!.y)
  for (const name of ['Recorded performance', 'Risk of the current holdings', 'Allocation over time']) await expect(page.getByRole('region', { name })).toBeVisible()
  // Background map: this US-only portfolio lights New York only; the portfolio list lights every exchange city.
  const map = page.getByTestId('world-map')
  await expect(map.locator('[data-country="US"]')).toHaveAttribute('data-state', 'lit')
  await expect(map.locator('[data-country="JP"]')).toHaveAttribute('data-state', 'idle')
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/overview.png`, fullPage: true })
  await page.goto('/')
  await expect(map.locator('[data-state="lit"]')).toHaveCount(16)
})
