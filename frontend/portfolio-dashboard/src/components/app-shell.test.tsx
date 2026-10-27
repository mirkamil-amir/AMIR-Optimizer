import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import { SelectionProvider } from '@/lib/selection'
import type { CurrentUser } from '@/lib/types'
import { AppShell } from './app-shell'

const auth = vi.hoisted(() => ({ current: { user: null as CurrentUser | null, isAdministrator: false, logout: async () => {} } }))
vi.mock('@/lib/auth', () => ({ useAuth: () => auth.current }))
vi.mock('@/lib/portfolio-context', () => ({ usePortfolioContext: () => ({ portfolios: [], portfolioId: null, select: () => {} }) }))
vi.mock('@/lib/queries', () => ({ useMarketSummary: () => ({ data: undefined }) }))
vi.mock('@/components/theme-controls', () => ({ ThemeQuickToggle: () => null }))

const user = (isAdministrator: boolean): CurrentUser => ({
  id: '1', email: 'u@example.test', displayName: 'U', status: 'Approved', roles: isAdministrator ? ['Administrator'] : ['User'],
  isAdministrator, createdAtUtc: '2026-01-01T00:00:00Z',
})

const PAGES = ['/', '/learn', '/learn/glossary', '/learn/:strategyId', '/research', '/news', '/watchlist', '/market-data', '/settings', '/stocks', '/stocks/:symbol', '/backtest', '/optimize', '/admin/settings']

function renderAt(path: string) {
  const router = createMemoryRouter(
    [{ element: <AppShell />, children: PAGES.map((p) => ({ path: p, element: <p>page {p}</p> })) }],
    { initialEntries: [path] },
  )
  render(<TooltipProvider><SelectionProvider><RouterProvider router={router} /></SelectionProvider></TooltipProvider>)
  return router
}

const primary = () => screen.getByRole('navigation', { name: 'Primary' })
const optimizerNav = () => screen.getAllByRole('navigation', { name: 'Viewer sections' })[0]
const link = (nav: HTMLElement, name: string) => within(nav).getByRole('link', { name })

describe('application shell navigation', () => {
  beforeEach(() => {
    auth.current = { user: user(false), isAdministrator: false, logout: async () => {} }
  })

  it('shows the eight primary destinations in order and marks the current one', () => {
    renderAt('/')
    expect(within(primary()).getAllByRole('link').map((a) => a.textContent)).toEqual(
      ['Viewer', 'AI Research', 'Financial News', 'Watchlist', 'Market Data', 'Strategy Guide', 'Glossary', 'Settings'],
    )
    expect(link(primary(), 'Viewer')).toHaveAttribute('aria-current', 'page')
    expect(link(optimizerNav(), 'Lens')).toHaveAttribute('aria-current', 'page')
  })

  it('marks Optimizer as the area of a child section and highlights that section', () => {
    renderAt('/stocks/AAPL')
    expect(link(primary(), 'Viewer')).toHaveAttribute('aria-current', 'true')
    expect(link(optimizerNav(), 'Forge')).toHaveAttribute('aria-current', 'page')
    expect(link(primary(), 'Watchlist')).not.toHaveAttribute('aria-current')
  })

  it('opens each primary destination, and back/forward keep the active item in step', async () => {
    const router = renderAt('/backtest')
    for (const [name, path] of [['AI Research', '/research'], ['Financial News', '/news'], ['Watchlist', '/watchlist'], ['Market Data', '/market-data'], ['Strategy Guide', '/learn'], ['Glossary', '/learn/glossary'], ['Settings', '/settings']]) {
      await userEvent.click(link(primary(), name))
      expect(screen.getByText(`page ${path}`)).toBeInTheDocument()
      expect(link(primary(), name)).toHaveAttribute('aria-current', 'page')
      expect(link(primary(), 'Viewer')).not.toHaveAttribute('aria-current')
      expect(within(optimizerNav()).queryByRole('link', { current: 'page' })).toBeNull()
    }
    await userEvent.click(link(primary(), 'Viewer'))
    expect(screen.getByText('page /')).toBeInTheDocument()

    await act(() => router.navigate(-1))
    expect(link(primary(), 'Settings')).toHaveAttribute('aria-current', 'page')
    await act(() => router.navigate(1))
    expect(link(primary(), 'Viewer')).toHaveAttribute('aria-current', 'page')
  })

  it('keeps the open Optimizer section when Optimizer is selected again', async () => {
    const router = renderAt('/backtest')
    await userEvent.click(link(primary(), 'Viewer'))
    expect(router.state.location.pathname).toBe('/backtest')
    expect(screen.getByText('page /backtest')).toBeInTheDocument()
  })

  it('marks the strategy guide as the area of a single strategy page', () => {
    renderAt('/learn/max-sharpe')
    expect(link(primary(), 'Strategy Guide')).toHaveAttribute('aria-current', 'true')
    expect(link(primary(), 'Glossary')).not.toHaveAttribute('aria-current')
  })

  it('does not list the moved destinations in the sidebar', () => {
    renderAt('/')
    for (const name of [/AI research/i, /Financial news/i, /Watchlist/i, /Market data/i, /Strategy guide/i, /Glossary/i, /^Settings$/i]) {
      expect(within(optimizerNav()).queryByRole('link', { name })).toBeNull()
    }
  })

  it('shows no administration area to regular users', () => {
    renderAt('/')
    expect(screen.queryByRole('navigation', { name: 'Administration' })).toBeNull()
    expect(screen.queryByRole('link', { name: 'Admin settings' })).toBeNull()
  })

  it('keeps Admin settings in the sidebar administration area for administrators, not in the top bar', () => {
    auth.current = { user: user(true), isAdministrator: true, logout: async () => {} }
    renderAt('/admin/settings')
    const admin = screen.getAllByRole('navigation', { name: 'Administration' })[0]
    expect(link(admin, 'Admin settings')).toHaveAttribute('aria-current', 'page')
    expect(within(primary()).queryByRole('link', { name: /admin/i })).toBeNull()
    expect(within(primary()).queryByRole('link', { current: 'page' })).toBeNull()
    expect(within(primary()).queryByRole('link', { current: true })).toBeNull()
  })
})
