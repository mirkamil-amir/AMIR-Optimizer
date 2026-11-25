import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import type { StockFinancials } from '@/lib/types'
import { AccountingCard } from './accounting-card'

const available = (symbol: string): StockFinancials => ({
  symbol, status: 'Available', usesFinancialMeasures: false, retrievedAtUtc: '2026-11-25T12:00:00Z', isSimulated: false, message: null,
  annual: {
    periodType: 'FY', periodEnds: ['2025-12-31', '2024-12-31'],
    sections: [
      { key: 'income', title: 'Income statement', rows: [{ key: 'TotalRevenue', label: 'Revenue', kind: 'amount', currency: 'EUR', values: [1.1e9, 1e9], notes: [null, null] }] },
      { key: 'ratios', title: 'Ratios', rows: [{ key: 'debtEquity', label: 'Debt/equity', kind: 'multiple', currency: null, values: [null, 0.5], notes: ['Not meaningful: the denominator is negative.', null] }] },
    ],
  },
  quarterly: {
    periodType: 'Q', periodEnds: ['2026-09-30'],
    sections: [{ key: 'income', title: 'Income statement', rows: [{ key: 'TotalRevenue', label: 'Revenue', kind: 'amount', currency: 'EUR', values: [3e8], notes: [null] }] }],
  },
  valuation: [{ key: 'MarketCap', label: 'Market cap', kind: 'amount', value: 2e10, currency: 'EUR', asOf: '2026-11-20' }],
})

let respond: (symbol: string) => Response
const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

beforeEach(() => {
  respond = (s) => ok(available(s))
  vi.stubGlobal('fetch', vi.fn((url: string) => Promise.resolve(respond(decodeURIComponent(String(url).split('/stocks/')[1].split('/')[0])))))
})
afterEach(() => vi.unstubAllGlobals())

function show(symbol = 'SAP.DE') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(<QueryClientProvider client={client}><TooltipProvider><AccountingCard symbol={symbol} /></TooltipProvider></QueryClientProvider>)
}

describe('AccountingCard', () => {
  it('shows fiscal years newest first in the reporting currency, then quarters and other statements', async () => {
    const user = userEvent.setup()
    show()
    const table = await screen.findByRole('table', { name: /Income statement of SAP.DE, fiscal years/ })
    const headers = within(table).getAllByRole('columnheader').map((h) => h.textContent)
    expect(headers).toEqual(['Income statement', 'FY 2025', 'FY 2024'])
    expect(within(table).getByText('€1.1B')).toBeInTheDocument()
    expect(screen.getByText('Reported in EUR')).toBeInTheDocument()
    expect(screen.getByText('€20B')).toBeInTheDocument() // valuation

    await user.click(screen.getByRole('radio', { name: 'Ratios' }))
    const ratios = screen.getByRole('table', { name: /Ratios of SAP.DE/ })
    expect(within(ratios).getByTitle('Not meaningful: the denominator is negative.')).toHaveTextContent('—')
    expect(within(ratios).getByText('0.5×')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: 'Quarters' }))
    const q = screen.getByRole('table', { name: /quarters/ })
    expect(within(q).getByText('Sep 2026')).toBeInTheDocument()
    expect(within(q).getByText('€300M')).toBeInTheDocument()
  })

  it('reports pending, not applicable and unavailable figures instead of a table', async () => {
    respond = (s) => ok({ ...available(s), status: 'Pending', annual: null, quarterly: null, valuation: [], message: 'Company figures are being retrieved.' })
    const { unmount } = show()
    expect(await screen.findByText('Company figures are being retrieved…')).toBeInTheDocument()
    unmount()

    respond = (s) => ok({ ...available(s), status: 'NotApplicable', annual: null, quarterly: null, valuation: [] })
    const second = show('SPY')
    expect(await screen.findByText('No accounting figures for ETFs and indices.')).toBeInTheDocument()
    second.unmount()

    respond = (s) => ok({ ...available(s), status: 'NotFound', annual: null, quarterly: null, valuation: [], message: 'Company figures are unavailable: the source has none.' })
    show('XYZ')
    expect(await screen.findByText('Company figures are unavailable: the source has none.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('shows an error with a retry when the request fails', async () => {
    respond = () => new Response(JSON.stringify({ title: 'Server error', detail: 'Simulated' }), { status: 500 })
    show()
    await waitFor(() => expect(screen.getByRole('button', { name: /retry/i })).toBeInTheDocument())
  })
})
