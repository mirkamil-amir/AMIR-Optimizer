import { expect, test, type Page } from '@playwright/test'
import { acceptDownloadIfAsked, admin, approve, register, requireAdminCredentials, signIn, signOut, strongPassword, uniqueEmail } from './helpers'

// Matrix Pilot on the e2e stack (docs/matrix-pilot.md). The stack runs the pilot with the deterministic test script
// (MATRIX_PILOT_PROVIDER=Mock – labelled as a test model, not an AI) and no scheduler; everything else is the production
// path: mandate, confirmed activation, a decision cycle with real tools, validation, simulated booking, journal, kill
// switch, audit trail and the live record in the portfolio's Arena tab.
test.beforeAll(requireAdminCredentials)

async function newStudent(page: Page) {
  const email = uniqueEmail('pilot')
  await register(page, 'E2E Pilot Owner', email)
  await signIn(page, admin.email, admin.password)
  await approve(page, email)
  await signOut(page)
  await signIn(page, email, strongPassword)
}

async function createPortfolio(page: Page, name: string, symbols: string[]) {
  await page.goto('/')
  await page.getByRole('link', { name: 'Forge a portfolio' }).first().click()
  await page.getByLabel('Name', { exact: true }).fill(name)
  const search = page.getByLabel('Search company name or ticker')
  for (const s of symbols) {
    await search.fill(s)
    await page.getByRole('checkbox', { name: `Select ${s}`, exact: true }).check()
  }
  await page.getByRole('button', { name: 'Forge portfolio' }).click()
  await expect(page).toHaveURL(/\/portfolios\/[0-9a-f-]{36}\?created=1$/)
  return page.url().match(/portfolios\/([0-9a-f-]{36})/)![1]
}

test('Matrix Pilot: set up, confirm, decide, book, inspect, kill switch, live record', async ({ page }) => {
  test.setTimeout(240_000)
  await newStudent(page)
  // From here on (signed in) no script errors – as in portfolios.spec.ts.
  const errors: string[] = []
  page.on('pageerror', (e) => errors.push(e.message))
  const id = await createPortfolio(page, 'Pilot E2E', ['AAPL', 'MSFT', 'JPM', 'PG', 'KO', 'JNJ'])

  // Make sure the universe has stored history (the stack downloads simulated prices on request).
  await page.getByRole('navigation', { name: 'Portfolio sections' }).getByRole('link', { name: 'Solver', exact: true }).click()
  await page.getByRole('button', { name: 'Solve', exact: true }).click()
  await acceptDownloadIfAsked(page)
  await expect(page.getByText('What your result means')).toBeVisible({ timeout: 60_000 })

  // AI Probe › Matrix Pilot lists the portfolio, not set up yet.
  await page.getByRole('navigation', { name: 'Primary' }).getByRole('link', { name: 'AI Probe' }).click()
  await page.getByRole('navigation', { name: 'AI Probe' }).getByRole('link', { name: 'Matrix Pilot' }).click()
  await expect(page).toHaveURL(/\/research\/pilot$/)
  await expect(page.getByText(/deterministic test script/)).toBeVisible()
  await expect(page.getByRole('row', { name: /Pilot E2E/ })).toContainText('not set up')
  await page.getByRole('link', { name: 'Pilot E2E' }).click()
  await expect(page).toHaveURL(new RegExp(`/research/pilot/${id}$`))

  // Set up with the suggested mandate (switched off), then the explicit confirmation.
  await expect(page.getByLabel('Max position %')).toHaveValue('25')
  await page.getByRole('button', { name: 'Set up Matrix Pilot' }).click()
  await expect(page.getByText('DISABLED').first()).toBeVisible()
  await page.getByRole('button', { name: 'Switch Matrix Pilot on' }).click()
  const confirm = page.getByRole('dialog', { name: 'Enable autonomous AI portfolio management' })
  await expect(confirm).toContainText('Pilot E2E (USD)')
  await expect(confirm).toContainText('test script – not an AI')
  await expect(confirm).toContainText('validate every allocation')
  const go = confirm.getByRole('button', { name: 'Switch Matrix Pilot on' })
  await expect(go).toBeDisabled() // not without ticking "I understand"
  await confirm.getByRole('checkbox', { name: 'I understand' }).check()
  await go.click()
  await expect(page.getByText('ACTIVE').first()).toBeVisible()

  // One decision cycle: research with tools → decision → review → validation → simulated booking.
  await page.getByRole('button', { name: 'Run decision now' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Run decision now' }).click()
  const timeline = page.getByRole('region', { name: 'Decision timeline' })
  await expect(timeline.getByText('REBALANCED', { exact: true })).toBeVisible({ timeout: 90_000 })

  const decision = page.getByRole('region', { name: 'AI decision' })
  await expect(decision).toContainText('REBALANCE')
  await expect(decision.getByRole('columnheader', { name: 'AI proposed' })).toBeVisible()
  await expect(decision.getByRole('columnheader', { name: 'Held after booking' })).toBeVisible()
  await expect(decision).toContainText('Validation by the platform: passed')
  await expect(decision).toContainText('Fit to the mandate: the first proposal fitted every rule')
  const free = page.getByRole('region', { name: 'Unconstrained portfolio' })
  await expect(free).toContainText('not traded')
  await expect(free.getByText('Mandate portfolio vs unconstrained portfolio')).toBeVisible()

  const activity = page.getByRole('region', { name: 'AI activity' })
  await expect(activity.getByRole('cell', { name: 'screen_universe', exact: true })).toBeVisible()
  await expect(activity.getByRole('cell', { name: 'Simulated booking', exact: true })).toBeVisible()
  await expect(activity.getByRole('cell', { name: 'AI DECISION', exact: true }).first()).toBeVisible()
  await activity.getByRole('row', { name: /^\d+ DETERMINISTIC CALCULATION \(tool requested by the AI\) screen_universe/ }).getByRole('button', { name: 'Show data' }).click()
  await expect(activity.getByText(/"sortBy": "return_per_volatility"/).first()).toBeVisible()

  // Running again in the same period is refused (one decision per period).
  await page.getByRole('button', { name: 'Run decision now' }).click()
  await page.getByRole('dialog').getByRole('button', { name: 'Run decision now' }).click()
  await expect(page.getByText(/already has a decision/)).toBeVisible()
  await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click()

  // Kill switch: stops at once, recorded in the audit trail.
  await page.getByRole('button', { name: 'Kill switch' }).click()
  const stop = page.getByRole('dialog', { name: 'Kill switch' })
  await stop.getByLabel('Reason (recorded in the audit trail)').fill('E2E stop')
  await stop.getByRole('button', { name: 'Stop Matrix Pilot now' }).click()
  await expect(page.getByText('KILL SWITCH').first()).toBeVisible()
  await expect(page.getByText('Stopped by the kill switch')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Run decision now' })).toBeHidden()
  await page.getByRole('button', { name: 'Audit trail' }).click()
  await expect(page.getByRole('cell', { name: 'kill_switch', exact: true })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'E2E stop', exact: true })).toBeVisible()

  // The simulated trades are in the ledger; the live record is in the portfolio's Arena tab, starting at the baseline.
  const holdings = (await (await page.request.get(`/api/v1/portfolios/${id}/holdings`)).json()) as { holdings: { quantity: number }[] }
  expect(holdings.holdings.filter((h) => h.quantity > 0).length).toBeGreaterThanOrEqual(5)
  await page.goto(`/portfolios/${id}/comparison`)
  const live = page.getByRole('region', { name: 'Live record: Matrix Pilot' })
  await expect(live).toBeVisible()
  await expect(live.getByRole('columnheader', { name: 'Matrix Pilot' }).first()).toBeVisible()
  await expect(live).toContainText('Live record only')
  await expect(live.getByRole('cell', { name: 'Trades', exact: true })).toBeVisible()
  await page.getByRole('link', { name: 'Open Matrix Pilot' }).click()
  await expect(page).toHaveURL(new RegExp(`/research/pilot/${id}$`))

  expect(errors).toEqual([])
})
