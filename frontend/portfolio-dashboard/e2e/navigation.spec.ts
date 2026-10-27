import { expect, test, type Page } from '@playwright/test'
import { ADMIN_STATE, admin, approve, register, requireAdminCredentials, signIn, signOut, strongPassword, uniqueEmail } from './helpers'

// Application navigation: eight primary areas in the top bar, Viewer sections and administration in the sidebar.
const PRIMARY = ['Viewer', 'AI Research', 'Financial News', 'Watchlist', 'Market Data', 'Strategy Guide', 'Glossary', 'Settings']

const primary = (page: Page) => page.getByRole('navigation', { name: 'Primary' })
const optimizer = (page: Page) => page.getByRole('navigation', { name: 'Viewer sections' }).first()

function collectErrors(page: Page) {
  const errors: string[] = []
  page.on('pageerror', (e) => errors.push(e.message))
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()) })
  return errors
}

test.describe('as administrator', () => {
  test.use({ storageState: ADMIN_STATE })

  test('primary areas, Viewer sections, admin settings, deep links and history', async ({ page }) => {
    const errors = collectErrors(page)
    await page.goto('/')
    await expect(primary(page).getByRole('link')).toHaveText(PRIMARY)
    await expect(primary(page).getByRole('link', { name: 'Viewer' })).toHaveAttribute('aria-current', 'page')

    for (const [name, heading, url] of [
      ['AI Research', 'AI research assistant', /\/research$/],
      ['Financial News', 'Financial news', /\/news$/],
      ['Watchlist', 'Watchlist', /\/watchlist$/],
      ['Market Data', 'Market data', /\/market-data$/],
      ['Strategy Guide', 'Strategy guide', /\/learn$/],
      ['Glossary', 'Glossary', /\/learn\/glossary$/],
      ['Settings', 'Settings', /\/settings$/],
    ] as const) {
      await primary(page).getByRole('link', { name }).click()
      await expect(page).toHaveURL(url)
      await expect(page.getByRole('heading', { name: heading, exact: true })).toBeVisible()
      await expect(primary(page).getByRole('link', { name })).toHaveAttribute('aria-current', 'page')
    }
    // Ordinary settings carry no administrator form.
    await expect(page.getByRole('main')).not.toContainText('Application settings')

    // Market Data has three pages with the same search and paging; Market Data stays active on each.
    await primary(page).getByRole('link', { name: 'Market Data' }).click()
    const dataSections = page.getByRole('navigation', { name: 'Market data sections' })
    for (const [name, url, search] of [
      ['Indexes', /\/market-data\/indexes$/, 'Search indexes'],
      ['Currencies', /\/market-data\/currencies$/, 'Search currencies'],
      ['Stocks & ETFs', /\/market-data$/, 'Search stocks & ETFs'],
    ] as const) {
      await dataSections.getByRole('link', { name: new RegExp(`^${name}`) }).click()
      await expect(page).toHaveURL(url)
      await expect(dataSections.getByRole('link', { name: new RegExp(`^${name}`) })).toHaveAttribute('aria-current', 'page')
      await expect(page.getByRole('textbox', { name: search })).toBeVisible()
      await expect(page.getByRole('button', { name: `Sync ${name.toLowerCase()}` })).toBeVisible()   // each page syncs its own data
      // "page" on /market-data itself, "true" (this area) on its subpages.
      await expect(primary(page).getByRole('link', { name: 'Market Data' })).toHaveAttribute('aria-current', /^(page|true)$/)
    }
    await expect(page.getByRole('button', { name: 'Last page' })).toBeVisible()
    await expect(page.getByRole('combobox', { name: 'Rows per page' })).toBeVisible()

    // Optimizer opens its overview; its sections live in the sidebar and keep Optimizer active.
    await primary(page).getByRole('link', { name: 'Viewer' }).click()
    await expect(page).toHaveURL(/\/$/)
    for (const [name, heading, url] of [
      ['Forge', 'New portfolio', /\/stocks$/],
      ['Rewind', 'Backtesting', /\/backtest$/],
      ['Trace', 'History', /\/history$/],
      ['Pattern', 'ML Optimization', /\/portfolios\/[0-9a-f-]{36}\/ml-optimize$/],
      ['Arena', 'Comparison', /\/strategy-comparison$/],
    ] as const) {
      await optimizer(page).getByRole('link', { name, exact: true }).click()
      await expect(page).toHaveURL(url)
      await expect(page.getByRole('heading', { name: heading, exact: true })).toBeVisible()
      await expect(optimizer(page).getByRole('link', { name, exact: true })).toHaveAttribute('aria-current', 'page')
      await expect(primary(page).getByRole('link', { name: 'Viewer' })).toHaveAttribute('aria-current', 'true')
    }

    // Selecting Optimizer inside the workspace keeps the open section.
    await primary(page).getByRole('link', { name: 'Viewer' }).click()
    await expect(page).toHaveURL(/\/strategy-comparison$/)

    // Back and forward follow the history; the active items follow the URL.
    await page.goBack()
    await expect(page).toHaveURL(/\/ml-optimize$/)
    await expect(optimizer(page).getByRole('link', { name: 'Pattern', exact: true })).toHaveAttribute('aria-current', 'page')
    await page.goForward()
    await expect(page).toHaveURL(/\/strategy-comparison$/)

    // Admin settings stays in the sidebar's administration area – never in the top bar.
    const administration = page.getByRole('navigation', { name: 'Administration' }).first()
    await expect(administration.getByRole('link')).toHaveText(['Admin overview', 'Users', 'Admin settings'])
    await expect(primary(page).getByRole('link', { name: /admin/i })).toHaveCount(0)
    await administration.getByRole('link', { name: 'Admin settings' }).click()
    await expect(page).toHaveURL(/\/admin\/settings$/)
    await expect(page.getByRole('heading', { name: 'Administration', exact: true })).toBeVisible()
    await expect(primary(page).locator('[aria-current]')).toHaveCount(0)

    // Deep links survive a reload.
    for (const [path, name] of [['/news', 'Financial News'], ['/backtest', 'Viewer']] as const) {
      await page.goto(path)
      await page.reload()
      await expect(primary(page).getByRole('link', { name })).toHaveAttribute('aria-current', /page|true/)
    }
    expect(errors).toEqual([])
  })

  for (const [label, width, height] of [['desktop', 1440, 900], ['tablet', 820, 1180], ['mobile', 390, 844]] as const) {
    test(`layout at ${label} width`, async ({ page }) => {
      await page.setViewportSize({ width, height })
      await page.goto('/backtest')
      await expect(page.getByRole('heading', { name: 'Backtesting' })).toBeVisible()
      // No horizontal page scrolling; the header does not cover the page content.
      expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width)
      const header = await page.locator('header').boundingBox()
      const heading = await page.getByRole('heading', { name: 'Backtesting' }).boundingBox()
      expect(heading!.y).toBeGreaterThanOrEqual(header!.y + header!.height)
      // Every primary destination is reachable (the bar scrolls horizontally when narrow).
      for (const name of PRIMARY) {
        const link = primary(page).getByRole('link', { name })
        await link.scrollIntoViewIfNeeded()
        await expect(link).toBeInViewport()
      }
      if (width < 1024) {
        // Narrow screens: the sidebar is a menu sheet opened from the header.
        await expect(optimizer(page)).toBeHidden()
        await page.getByRole('button', { name: 'Open Viewer and administration menu' }).click()
        const sheet = page.getByRole('dialog')
        await sheet.getByRole('link', { name: 'Arena', exact: true }).click()
        await expect(page).toHaveURL(/\/strategy-comparison$/)
        await expect(sheet).toBeHidden()
      } else {
        await expect(optimizer(page)).toBeVisible()
      }
      // Keyboard: the primary links are focusable in order.
      await primary(page).getByRole('link', { name: 'Viewer' }).focus()
      await page.keyboard.press('Tab')
      await expect(primary(page).getByRole('link', { name: 'AI Research' })).toBeFocused()
    })
  }
})

test.describe('as a regular user', () => {
  test.beforeAll(requireAdminCredentials)

  test('no administration area, and the server refuses admin requests', async ({ page }) => {
    const email = uniqueEmail('navigator')
    await register(page, 'E2E Navigator', email)
    await signIn(page, admin.email, admin.password)
    await approve(page, email)
    await signOut(page)
    await signIn(page, email, strongPassword)

    await expect(primary(page).getByRole('link')).toHaveText(PRIMARY)
    await expect(page.getByRole('navigation', { name: 'Administration' })).toHaveCount(0)
    await expect(page.getByRole('link', { name: 'Admin settings' })).toHaveCount(0)
    await primary(page).getByRole('link', { name: 'Settings' }).click()
    await expect(page.getByRole('heading', { name: 'Settings', exact: true })).toBeVisible()
    await expect(page.getByRole('main')).not.toContainText('Application settings')

    await page.goto('/admin/settings')
    await expect(page.getByText('Administrators only')).toBeVisible()
    expect((await page.request.get('/api/v1/admin/users')).status()).toBe(403)
    expect((await page.request.get('/api/v1/admin/overview')).status()).toBe(403)
  })
})
