import { useState } from 'react'
import { act, render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { createMemoryRouter, RouterProvider } from 'react-router'
import { describe, expect, it, vi } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import { PortfolioProvider, usePortfolioContext } from '@/lib/portfolio-context'
import type { PortfolioSummary } from '@/lib/types'
import { PortfolioWorkspace, ToCurrentPortfolio } from './portfolio-workspace'

const summary = (id: string, name: string, extra: Partial<PortfolioSummary> = {}): PortfolioSummary => ({
  id, name, baseCurrency: 'USD', initialInvestment: 1000, cashBalance: 1000, marketValue: 0, totalValue: 1000, holdings: 0,
  createdAtUtc: '2026-10-01T00:00:00Z', updatedAtUtc: '2026-10-01T00:00:00Z', valuationProblems: [], description: null, status: 'Active',
  archivedAtUtc: null, netContributions: 1000, profitLoss: 0, valuationDate: null, stalePositions: 0, usesSimulatedData: true,
  latestOptimizationAtUtc: null, lastActivityAtUtc: null, ...extra,
})

const data = vi.hoisted(() => ({ portfolios: [] as PortfolioSummary[] }))
const idle = { mutateAsync: vi.fn(), isPending: false }
vi.mock('@/lib/queries', () => ({
  usePortfolios: () => ({ data: data.portfolios, isLoading: false }),
  usePortfolio: () => ({ data: undefined }),
  useArchivePortfolio: () => idle,
  useUpdatePortfolioDetails: () => idle,
  useRecordTransaction: () => idle,
  useRun: () => ({ data: undefined }),
  useStocks: () => ({ data: undefined }),
}))

/** A section with its own transient state, standing in for e.g. the optimizer form. */
function Section() {
  const { portfolioId, scoped } = usePortfolioContext()
  const [draft, setDraft] = useState('')
  return (
    <div>
      <p>section of {portfolioId} {scoped ? '(scoped)' : ''}</p>
      <input aria-label="draft" value={draft} onChange={(e) => setDraft(e.target.value)} />
    </div>
  )
}

function renderAt(path: string) {
  const router = createMemoryRouter([
    { path: '/', element: <p>my portfolios</p> },
    { path: '/holdings', element: <ToCurrentPortfolio section="holdings" /> },
    { path: '/portfolios/:portfolioId', element: <PortfolioWorkspace />, children: [{ index: true, element: <Section /> }, { path: 'holdings', element: <Section /> }] },
  ], { initialEntries: [path] })
  render(<TooltipProvider><PortfolioProvider><RouterProvider router={router} /></PortfolioProvider></TooltipProvider>)
  return router
}

describe('portfolio workspace', () => {
  it('takes the portfolio from the URL and names it in the header', async () => {
    data.portfolios = [summary('a', 'Core'), summary('b', 'Tech', { description: 'Research ideas' })]
    renderAt('/portfolios/b')
    expect(await screen.findByRole('heading', { level: 1, name: 'Tech' })).toBeInTheDocument()
    expect(screen.getByText('Research ideas')).toBeInTheDocument()
    expect(screen.getByText('section of b (scoped)')).toBeInTheDocument()
    expect(within(screen.getByRole('navigation', { name: 'Portfolio sections' })).getByRole('link', { name: 'Overview' })).toHaveAttribute('aria-current', 'page')
  })

  it('shows a safe not-found state for an unknown or foreign id', async () => {
    data.portfolios = [summary('a', 'Core')]
    renderAt('/portfolios/someone-elses')
    expect(await screen.findByText('Portfolio not found')).toBeInTheDocument()
    expect(screen.queryByText(/section of/)).toBeNull()
  })

  it('resets section state when switching to another portfolio', async () => {
    data.portfolios = [summary('a', 'Core'), summary('b', 'Tech')]
    const router = renderAt('/portfolios/a/holdings')
    await userEvent.type(await screen.findByLabelText('draft'), 'weights for A')
    expect(screen.getByLabelText('draft')).toHaveValue('weights for A')
    await act(() => router.navigate('/portfolios/b/holdings'))
    expect(screen.getByText('section of b (scoped)')).toBeInTheDocument()
    expect(screen.getByLabelText('draft')).toHaveValue('')
    expect(screen.getByRole('heading', { level: 1, name: 'Tech' })).toBeInTheDocument()
  })

  it('marks an archived portfolio as read-only and offers no booking', async () => {
    data.portfolios = [summary('a', 'Old', { status: 'Archived', archivedAtUtc: '2026-10-10T00:00:00Z' })]
    renderAt('/portfolios/a')
    expect(await screen.findByText(/archived: its history is read-only/i)).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Record transaction' })).toBeNull()
  })

  it('sends an old portfolio-less URL to the most recently opened active portfolio', async () => {
    data.portfolios = [summary('x', 'Archived one', { status: 'Archived' }), summary('a', 'Core')]
    const router = renderAt('/holdings')
    expect(await screen.findByText('section of a (scoped)')).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/portfolios/a/holdings')
  })

  it('sends an old URL to the portfolio list when the user has no portfolio', async () => {
    data.portfolios = []
    const router = renderAt('/holdings')
    expect(await screen.findByText('my portfolios')).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/')
  })
})
