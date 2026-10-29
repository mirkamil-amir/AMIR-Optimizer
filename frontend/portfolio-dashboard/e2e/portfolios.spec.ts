import { expect, test, type Page } from '@playwright/test'
import { admin, approve, register, requireAdminCredentials, signIn, signOut, strongPassword, uniqueEmail, acceptDownloadIfAsked } from './helpers'

// Several independent portfolios per user: New portfolio, the overview, a workspace per portfolio, recorded
// transactions, isolation between portfolios and between users, deep links and narrow layouts.
test.beforeAll(requireAdminCredentials)

async function newStudent(page: Page, name: string) {
  const email = uniqueEmail('portfolios')
  await register(page, name, email)
  await signIn(page, admin.email, admin.password)
  await approve(page, email)
  await signOut(page)
  await signIn(page, email, strongPassword)
  return email
}

async function createPortfolio(page: Page, name: string, symbols: string[], description?: string) {
  await page.goto('/')
  await page.getByRole('link', { name: 'Forge a portfolio' }).first().click()
  await expect(page.getByRole('heading', { name: 'New portfolio' })).toBeVisible()
  await page.getByLabel('Name', { exact: true }).fill(name)
  if (description) await page.getByLabel('Description (optional)').fill(description)
  const search = page.getByLabel('Search company name or ticker')
  for (const s of symbols) {
    await search.fill(s)
    await page.getByRole('checkbox', { name: `Select ${s}` }).check()
  }
  await page.getByRole('button', { name: 'Forge portfolio' }).click()
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\?created=1$/)
  await expect(page.getByRole('heading', { level: 1, name })).toBeVisible()
  await expect(page.getByRole('region', { name: 'Next steps' })).toBeVisible()
  return page.url().match(/portfolios\/([0-9a-f-]{36})/)![1]
}

const sections = (page: Page) => page.getByRole('navigation', { name: 'Portfolio sections' })

test('two independent portfolios: create, optimize, record, switch, reload, isolate', async ({ page }) => {
  test.setTimeout(180_000)
  await newStudent(page, 'E2E Portfolio Owner')
  // From here on no script errors; the only failed request allowed is the deliberately refused withdrawal (422).
  const errors: string[] = []
  page.on('pageerror', (e) => errors.push(e.message))
  page.on('console', (m) => { if (m.type() === 'error' && !/status of 422/.test(m.text())) errors.push(m.text()) })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'My portfolios' })).toBeVisible()
  await expect(page.getByText('No portfolios yet')).toBeVisible()

  // Abandoning New portfolio stores nothing.
  await page.goto('/stocks')
  await page.getByLabel('Name', { exact: true }).fill('Never created')
  await page.goto('/')
  await expect(page.getByText('No portfolios yet')).toBeVisible()

  const a = await createPortfolio(page, 'Core A', ['AAPL', 'MSFT', 'JPM', 'XOM'], 'Long-term diversified')
  // The temporary selection was used for A and cleared.
  await expect(page.getByRole('link', { name: 'New portfolio: 0 stocks selected' })).toBeVisible()
  const b = await createPortfolio(page, 'Tech B', ['NVDA', 'GOOGL', 'AMZN'])
  expect(a).not.toBe(b)

  // The overview lists both, each with its own figures, and the honest total.
  await page.goto('/')
  const cards = page.getByRole('region', { name: 'Active portfolios' })
  await expect(cards.getByRole('link', { name: 'Core A', exact: true })).toBeVisible()
  await expect(cards.getByRole('link', { name: 'Tech B', exact: true })).toBeVisible()
  await expect(cards.getByText('Long-term diversified')).toBeVisible()
  await expect(page.getByRole('region', { name: 'Totals across active portfolios' })).toContainText('Across 2 active portfolios')

  // Portfolio A: optimize with its own universe.
  await page.getByRole('link', { name: 'Open Core A' }).click()
  await page.getByRole('navigation', { name: 'Portfolio sections' }).getByRole('link', { name: 'Solver', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${a}/optimize$`))
  await expect(page.getByRole('heading', { level: 1, name: 'Core A' })).toBeVisible()
  await page.getByRole('combobox', { name: 'Optimization method' }).click()
  await page.getByRole('option', { name: 'Minimum variance' }).click()
  await page.getByRole('button', { name: 'Solve', exact: true }).click()
  await acceptDownloadIfAsked(page)
  await expect(page.getByText('What your result means')).toBeVisible({ timeout: 30_000 })
  await expect(page).toHaveURL(new RegExp(`/portfolios/${a}/optimize/[0-9a-f-]{36}$`))
  const runUrl = page.url()
  const allocation = page.getByRole('table', { name: 'Recommended allocation' })
  await expect(allocation).not.toContainText('NVDA')
  // An optimization changes no holdings (the Overview's holdings may show target weights, but no position is held).
  const held = async (id: string) => ((await (await page.request.get(`/api/v1/portfolios/${id}/holdings`)).json()) as { holdings: { symbol: string; quantity: number }[] })
    .holdings.filter((h) => h.quantity > 0).map((h) => h.symbol)
  expect(await held(a)).toEqual([])
  await sections(page).getByRole('link', { name: 'Core' }).click()

  // Record a deposit and a buy in A.
  await page.getByRole('button', { name: 'Record transaction' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toContainText('Core A')
  await dialog.getByRole('combobox', { name: 'Transaction type' }).click()
  await page.getByRole('option', { name: 'Deposit cash' }).click()
  await dialog.getByLabel(/^Amount/).fill('500')
  await dialog.getByRole('button', { name: 'Record' }).click()
  await expect(page.getByText(/Deposit cash recorded in “Core A”/)).toBeVisible()

  await page.getByRole('button', { name: 'Record transaction' }).click()
  await dialog.getByLabel('Stock').fill('AAPL')
  await dialog.getByRole('button', { name: 'Use it' }).click()
  await dialog.getByLabel('Shares').fill('3')
  await dialog.getByRole('button', { name: 'Record' }).click()
  await expect(page.getByText(/Buy recorded in “Core A”/)).toBeVisible()
  await expect(page.getByRole('row', { name: /AAPL/ }).first()).toBeVisible()
  expect(await held(a)).toEqual(['AAPL'])

  // Invalid bookings are explained and change nothing.
  await page.getByRole('button', { name: 'Record transaction' }).click()
  await dialog.getByRole('combobox', { name: 'Transaction type' }).click()
  await page.getByRole('option', { name: 'Withdraw cash' }).click()
  await dialog.getByLabel(/^Amount/).fill('100000000')
  await dialog.getByRole('button', { name: 'Record' }).click()
  await expect(dialog.getByRole('alert')).toContainText(/Insufficient cash/)
  await dialog.getByRole('button', { name: 'Cancel' }).click()

  await sections(page).getByRole('link', { name: 'Ledger' }).click()
  const recorded = page.getByRole('table', { name: 'Recorded transactions' })
  await expect(recorded.getByRole('row')).toHaveCount(3) // header + deposit + buy

  // Switch to B: nothing of A appears (holdings, recorded transactions, runs).
  await page.getByRole('combobox', { name: 'Switch portfolio' }).click()
  await page.getByRole('option', { name: 'Tech B' }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${b}/transactions$`))
  await expect(page.getByRole('heading', { level: 1, name: 'Tech B' })).toBeVisible()
  await expect(page.getByText('Nothing recorded yet')).toBeVisible()
  await sections(page).getByRole('link', { name: 'Core' }).click()
  await expect(page.getByRole('region', { name: 'Holdings', exact: true })).toBeVisible()
  await expect(page.getByRole('row', { name: /AAPL/ })).toHaveCount(0)
  expect(await held(b)).toEqual([])
  await sections(page).getByRole('link', { name: 'Solver', exact: true }).click()
  await expect(page.getByText('No optimization yet')).toBeVisible()

  // Back to A: its run and history are still there; its Optimization runs tab lists only its own runs and stays inside it.
  await page.goto(runUrl)
  await expect(page.getByText('What your result means')).toBeVisible({ timeout: 30_000 })
  await sections(page).getByRole('link', { name: 'Trace', exact: true }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${a}/runs$`))
  await expect(page.getByRole('heading', { level: 1, name: 'Core A' })).toBeVisible()
  await expect(page.getByRole('combobox', { name: 'Portfolio filter' })).toHaveCount(0)
  await expect(page.getByRole('table', { name: 'Math Optimizations of this portfolio' }).getByRole('row')).toHaveCount(2)

  // A deep link survives a reload; back/forward keep the portfolio. The former Holdings URL opens the Overview.
  await page.goto(`/portfolios/${a}/holdings`)
  await expect(page).toHaveURL(new RegExp(`/portfolios/${a}$`))
  await page.reload()
  await expect(page.getByRole('heading', { level: 1, name: 'Core A' })).toBeVisible()
  await expect(page.getByRole('row', { name: /AAPL/ }).first()).toBeVisible()
  await page.goto(`/portfolios/${b}`)
  await page.goBack()
  await expect(page.getByRole('heading', { level: 1, name: 'Core A' })).toBeVisible()

  // Archive B: still listed (archived), read-only; restore brings it back.
  await page.goto(`/portfolios/${b}`)
  await page.getByRole('button', { name: 'More portfolio actions' }).click()
  await page.getByRole('menuitem', { name: 'Archive' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Archive' }).click()
  await expect(page.getByText(/archived: its history is read-only/i)).toBeVisible()
  await expect(page.getByRole('button', { name: 'Record transaction' })).toHaveCount(0)
  await page.goto('/')
  const archivedList = page.getByRole('region', { name: 'Archived portfolios' })
  await expect(archivedList).toContainText('Tech B')
  await archivedList.getByRole('button', { name: 'Restore Tech B' }).click()
  await expect(page.getByRole('region', { name: 'Archived portfolios' })).toHaveCount(0)

  // An active portfolio can be deleted from its card after a confirmation.
  await cards.getByRole('button', { name: 'Delete Tech B' }).click()
  await page.getByRole('alertdialog').getByRole('button', { name: 'Delete permanently' }).click()
  await expect(cards.getByRole('link', { name: 'Tech B', exact: true })).toHaveCount(0)
  await expect(cards.getByRole('link', { name: 'Core A', exact: true })).toBeVisible()

  expect(errors).toEqual([])
  await signOut(page)

  // Another user: sees none of these portfolios and cannot open them by URL; the server returns 404.
  await newStudent(page, 'E2E Other User')
  await page.goto('/')
  await expect(page.getByText('No portfolios yet')).toBeVisible()
  await page.goto(`/portfolios/${a}`)
  await expect(page.getByText('Portfolio not found')).toBeVisible()
  await expect(page.getByText('Core A')).toHaveCount(0)
  expect((await page.request.get(`/api/v1/portfolios/${a}`)).status()).toBe(404)
  expect((await page.request.get(`/api/v1/portfolios/${a}/journal`)).status()).toBe(404)
})

test('overview and workspace fit narrow screens', async ({ page }) => {
  test.setTimeout(120_000)
  await newStudent(page, 'E2E Narrow')
  const id = await createPortfolio(page, 'Phone portfolio', ['AAPL', 'MSFT'])
  for (const [width, height] of [[820, 1180], [390, 844]] as const) {
    await page.setViewportSize({ width, height })
    for (const path of ['/', `/portfolios/${id}`, `/portfolios/${id}/transactions`, '/stocks']) {
      await page.goto(path)
      await expect(page.locator('main h1').first()).toBeVisible()
      expect(await page.evaluate(() => document.documentElement.scrollWidth), `${path} at ${width}px`).toBeLessThanOrEqual(width)
    }
    // Every section of the workspace is reachable (the tab bar scrolls when narrow).
    await page.goto(`/portfolios/${id}`)
    for (const name of ['Core', 'Solver', 'Pattern', 'Trace', 'Rewind', 'Arena', 'Ledger']) {
      const link = sections(page).getByRole('link', { name, exact: true })
      await link.scrollIntoViewIfNeeded()
      await expect(link).toBeInViewport()
    }
  }
})

test('Math Optimization: choose its stocks on the stock list and come back', async ({ page }) => {
  test.setTimeout(120_000)
  await newStudent(page, 'E2E Universe Chooser')
  const id = await createPortfolio(page, 'Chooser', ['AAPL', 'MSFT'])
  await sections(page).getByRole('link', { name: 'Solver', exact: true }).click()
  const universe = page.getByRole('table', { name: 'Stocks in the universe' })
  await expect(universe.getByRole('button', { name: /^Remove / })).toHaveCount(2)
  await expect(page.getByText('Configuration', { exact: true })).toHaveCount(0)

  // Browse stocks: the New portfolio list without the creation form, starting with the two stocks.
  await page.getByRole('link', { name: 'Browse stocks' }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${id}/optimize/stocks$`))
  await expect(page.getByRole('heading', { name: 'Choose stocks' })).toBeVisible()
  await expect(page.getByRole('region', { name: 'New portfolio' })).toHaveCount(0)
  await expect(page.getByRole('list', { name: 'Selected tickers' })).toContainText('AAPL')
  await expect(page.getByRole('list', { name: 'Selected tickers' })).toContainText('MSFT')
  await page.getByLabel('Search company name or ticker').fill('NVDA')
  await page.getByRole('checkbox', { name: 'Select NVDA' }).check()
  await page.getByRole('button', { name: 'Use 3 stocks' }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${id}/optimize\\?from=selection$`))
  await expect(universe.getByRole('button', { name: /^Remove / })).toHaveCount(3)
  await expect(universe).toContainText('NVDA')

  // Back without using: the optimization is unchanged.
  await page.getByRole('link', { name: 'Browse stocks' }).click()
  await page.getByRole('link', { name: 'Back to Math Optimization' }).click()
  await expect(page).toHaveURL(new RegExp(`/portfolios/${id}/optimize$`))
})
