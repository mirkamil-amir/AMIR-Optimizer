import { expect, test, type Page } from '@playwright/test'
import { ADMIN_STATE, acceptDownloadIfAsked } from './helpers'

test.use({ storageState: ADMIN_STATE })

const sidebar = (page: Page) => page.getByRole('navigation', { name: 'Matrix sections' }).first()

async function runOptimization(page: Page, portfolioId: string, method: string) {
  await page.goto(`/portfolios/${portfolioId}/optimize`)
  await expect(page.getByRole('heading', { name: 'Math Optimization' })).toBeVisible()
  await page.getByRole('combobox', { name: 'Optimization method' }).click()
  await page.getByRole('option', { name: method }).click()
  await page.getByRole('button', { name: 'Solve', exact: true }).click()
  await acceptDownloadIfAsked(page)
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/optimize/[0-9a-f-]{36}$`), { timeout: 120_000 })
  // Only successful runs can be backtested or compared.
  await expect(page.getByText('What your result means')).toBeVisible({ timeout: 30_000 })
}

/**
 * Both optimization pages: every section visible at once (no tabs); the trade proposal right under the key figures, above
 * the results; one folded reference box last ("What your result means" with inputs & methodology and data notes inside).
 */
async function expectAllSectionsVisible(page: Page, sections: string[], screenshot: string) {
  await expect(page.getByRole('tab')).toHaveCount(0)
  await expect(page.getByRole('button', { name: /^What your result means/ })).toHaveAttribute('aria-expanded', 'false')
  for (const name of sections) await expect(page.getByRole('region', { name, exact: true })).toBeVisible()
  const proposal = await page.getByText('Trade proposal', { exact: true }).boundingBox()
  const allocation = await page.getByRole('region', { name: 'Allocation', exact: true }).boundingBox()
  const insights = await page.getByText('What your result means', { exact: true }).boundingBox()
  expect(proposal!.y).toBeLessThan(allocation!.y)
  expect(insights!.y).toBeGreaterThan(allocation!.y)
  // One reference box: interpretation (plain paragraphs, no badges) with inputs & methodology inside.
  const box = page.getByRole('button', { name: /^What your result means/ })
  await box.click()
  await expect(page.getByRole('region', { name: 'Inputs & methodology', exact: true })).toBeVisible()
  await expect(page.getByText('Calculated from the data.', { exact: true })).toBeVisible()
  await box.click()
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/${screenshot}`, fullPage: true })
}

test('optimizations of one portfolio: Math and ML pages alike, backtest any optimization, compare math vs math', async ({ page }) => {
  test.setTimeout(300_000)
  // Sidebar: Mathematical and ML Optimization sit together, before the history; testing and comparing follow.
  await page.goto('/')
  await expect(sidebar(page).getByRole('link')).toHaveText(['Lens', 'Forge', 'Solver', 'Pattern',
    'Trace', 'Rewind', 'Arena'])

  // A portfolio with two mathematical optimizations.
  const name = `Strategies ${Date.now()}`
  await page.getByRole('link', { name: 'Forge a portfolio' }).first().click()
  await page.getByLabel('Name', { exact: true }).fill(name)
  const search = page.getByLabel('Search company name or ticker')
  for (const s of ['AAPL', 'MSFT', 'NVDA', 'JPM', 'XOM', 'JNJ']) {
    await search.fill(s)
    await page.getByRole('checkbox', { name: `Select ${s}` }).check()
  }
  await page.getByRole('button', { name: 'Forge portfolio' }).click()
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\?created=1$/)
  const portfolioId = page.url().match(/portfolios\/([0-9a-f-]{36})/)![1]
  await runOptimization(page, portfolioId, 'Minimum variance')
  await runOptimization(page, portfolioId, 'Equal weight')
  await expectAllSectionsVisible(page, ['Allocation', 'Efficient frontier', 'Correlation', 'Risk contribution'], 'math-optimization.png')
  await expect(page.getByText('Weight by sector', { exact: true })).toBeVisible()
  // Optimization history sits above the stocks; picking the earlier run switches the results to it.
  const history = page.getByRole('main').getByText('Optimization history', { exact: true })
  expect((await history.boundingBox())!.y).toBeLessThan((await page.getByText('Equity universe').boundingBox())!.y)
  await page.getByRole('link', { name: /^Minimum variance/ }).click()
  await expect(page.getByRole('heading', { level: 2, name: 'Minimum variance' })).toBeVisible()
  await expect(page.getByRole('link', { name: /^Minimum variance/ })).toHaveAttribute('aria-current', 'true')

  // ML Optimization: a tab of the same portfolio, same layout, the portfolio's universe by default.
  await page.getByRole('navigation', { name: 'Portfolio sections' }).getByRole('link', { name: 'Pattern', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/ml-optimize$`))
  await page.getByLabel('History (years)').fill('5')
  await page.getByRole('button', { name: 'Learn patterns' }).click()
  await acceptDownloadIfAsked(page)
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/ml-optimize/[0-9a-f-]{36}$`), { timeout: 120_000 })
  await expect(page.getByRole('region', { name: 'Allocation' })).toBeVisible({ timeout: 180_000 })
  await expectAllSectionsVisible(page, ['Allocation', 'Validation & test', 'Walk-forward backtest (test period)', 'Explainability'], 'ml-optimization.png')
  // Every feature of the model is explained in plain language.
  await page.getByRole('button', { name: /^What the \d+ features mean/ }).click()
  await expect(page.getByRole('list', { name: 'Features of this model' }).getByRole('listitem').first()).toContainText('Why a model may use it')

  // Backtest: the box lists every optimization of the chosen portfolio; one is picked on its own (no custom strategy form).
  await sidebar(page).getByRole('link', { name: 'Rewind', exact: true }).click()
  await expect(page.getByText('Custom strategy')).toHaveCount(0)
  const box = page.getByRole('region', { name: 'Backtest an optimization' })
  await box.getByRole('combobox', { name: 'Portfolio' }).click()
  await page.getByRole('option', { name: new RegExp(`^${name}`) }).click()
  const choices = box.getByRole('radiogroup', { name: 'Optimization' }).getByRole('radio')
  await expect(choices).toHaveCount(3)                                  // two Math runs and one ML Optimization
  await box.getByRole('button', { name: /^ML/ }).click()
  await expect(choices).toHaveCount(1)
  await box.getByRole('button', { name: /^All/ }).click()
  await choices.filter({ hasText: 'Minimum Variance' }).click()
  await expect(choices.filter({ hasText: 'Minimum Variance' })).toHaveAttribute('aria-checked', 'true')
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/backtest-window.png` })
  await box.getByRole('button', { name: 'Rewind', exact: true }).click()
  await acceptDownloadIfAsked(page)
  await expect(page).toHaveURL(/\/backtest\/[0-9a-f-]{36}$/, { timeout: 120_000 })
  await expect(page.getByText(/Math: Minimum Variance/).first()).toBeVisible()
  // Monthly returns as a line chart; the rebalance log folded until opened.
  await expect(page.getByText('Monthly returns', { exact: true })).toBeVisible({ timeout: 120_000 })
  const log = page.getByRole('button', { name: /^Rebalance log/ })
  await expect(log).toHaveAttribute('aria-expanded', 'false')
  await log.click()
  await expect(page.getByRole('table', { name: 'Rebalance log' })).toBeVisible()
  // Data notes, methodology and the hypothetical-results caveat share one folded box.
  await page.getByRole('button', { name: /^Data notes & methodology/ }).click()
  await expect(page.getByRole('region', { name: 'Methodology' })).toBeVisible()
  await expect(page.getByText(/survivorship bias/).first()).toBeVisible()
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/backtest-result.png`, fullPage: true })

  // Comparison: two mathematical optimizations of the same portfolio (no hybrid – it needs one ML strategy).
  await sidebar(page).getByRole('link', { name: 'Arena', exact: true }).click()
  await page.getByRole('combobox', { name: 'Portfolio' }).click()
  await page.getByRole('option', { name: new RegExp(`^${name}`) }).click()
  await page.getByRole('combobox', { name: 'Strategy A' }).click()
  await page.getByRole('option').filter({ hasText: 'Minimum Variance' }).click()
  await page.getByRole('combobox', { name: 'Strategy B' }).click()
  await page.getByRole('option').filter({ hasText: 'Equal Weight' }).click()
  await expect(page.getByRole('switch', { name: 'Add hybrid (ML screen + mean-variance)' })).toBeDisabled()
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/comparison-form.png` })
  await page.getByRole('button', { name: 'Face off' }).click()
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
  await tabs.getByRole('link', { name: 'Rewind', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/backtest$`))
  await expect(page.getByRole('link', { name: /Math: Minimum Variance/ })).toBeVisible()
  await page.getByRole('link', { name: /Math: Minimum Variance/ }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/backtest/[0-9a-f-]{36}$`))
  await tabs.getByRole('link', { name: 'Arena', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/comparison$`))
  await expect(page.getByText(/^A: Minimum Variance/).first()).toBeVisible({ timeout: 60_000 })
  await expect(page.getByRole('combobox', { name: 'Portfolio', exact: true })).toHaveCount(0)   // the portfolio is fixed here
  await tabs.getByRole('link', { name: 'Trace', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${portfolioId}/runs$`))
  await expect(page.getByRole('table', { name: 'Math Optimizations of this portfolio' }).getByRole('row')).toHaveCount(3)
  await expect(page.getByRole('table', { name: 'ML Optimizations of this portfolio' }).getByRole('row')).toHaveCount(2)
  // Both lists are searchable and filterable in place (no pages).
  const mathRuns = page.getByRole('table', { name: 'Math Optimizations of this portfolio' }).getByRole('row')
  await page.getByRole('searchbox', { name: 'Search Math Optimizations' }).fill('minimum variance')
  await expect(mathRuns).toHaveCount(2)
  await page.getByRole('searchbox', { name: 'Search Math Optimizations' }).fill('')
  await page.getByRole('combobox', { name: 'Objective filter' }).click()
  await page.getByRole('option', { name: 'Equal weight', exact: true }).click()
  await expect(mathRuns).toHaveCount(2)
  await expect(page.getByRole('button', { name: 'Next page' })).toHaveCount(0)
  // A run is deleted with the bin in its row, after a confirmation.
  await page.getByRole('button', { name: /^Delete the Equal weight run of/ }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete' }).click()
  await expect(page.getByText('No matching runs')).toBeVisible()        // the filter still shows Equal weight only
  await page.getByRole('combobox', { name: 'Objective filter' }).click()
  await page.getByRole('option', { name: 'All objectives' }).click()
  await expect(mathRuns).toHaveCount(2)

  // Overview: the former Performance charts and the former Holdings page, each once; no efficient frontier any more.
  await tabs.getByRole('link', { name: 'Core' }).click()
  await expect(tabs.getByRole('link', { name: 'Performance' })).toHaveCount(0)
  const vsBenchmark = await page.getByText('Portfolio vs benchmark', { exact: true }).boundingBox()
  const holdingsSection = page.getByRole('region', { name: 'Holdings', exact: true })
  await expect(holdingsSection).toBeVisible({ timeout: 30_000 })
  expect((await holdingsSection.boundingBox())!.y).toBeGreaterThan(vsBenchmark!.y)
  await expect(holdingsSection.getByRole('textbox', { name: 'Search holdings' })).toBeVisible()
  await expect(page.getByText(/^Efficient frontier/)).toHaveCount(0)
  await expect(tabs.getByRole('link', { name: 'Holdings' })).toHaveCount(0)
  for (const name of ['Value', 'Performance', 'Risk', 'Performance history', 'Risk breakdown', 'Allocation over time']) await expect(page.getByRole('region', { name, exact: true })).toBeVisible()
  // Background map: this US-only portfolio lights New York only; the portfolio list lights every exchange city.
  const map = page.getByTestId('world-map')
  await expect(map.locator('[data-country="US"]')).toHaveAttribute('data-state', 'lit')
  await expect(map.locator('[data-country="JP"]')).toHaveAttribute('data-state', 'idle')
  if (process.env.SCREENSHOT_DIR) await page.screenshot({ path: `${process.env.SCREENSHOT_DIR}/overview.png`, fullPage: true })
  await page.goto('/')
  await expect(map.locator('[data-state="lit"]')).toHaveCount(36)                    // every exchange city of the 36 markets
})
