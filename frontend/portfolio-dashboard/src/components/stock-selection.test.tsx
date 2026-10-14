import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { TooltipProvider } from '@/components/ui/tooltip'
import type { StockQuote } from '@/lib/types'
import { QuoteCell, QuoteProvenance } from './stock-selection'

const quote = (overrides: Partial<StockQuote> = {}): StockQuote => ({
  symbol: 'AAPL', price: 200, currency: 'USD', kind: 'LastClose', priceDate: '2026-10-09', asOfUtc: null,
  previousClose: 190, previousCloseDate: '2026-10-08', change: 10, changePercent: 10 / 190,
  provider: 'Yahoo', retrievedAtUtc: '2026-10-10T00:00:00Z', isSimulated: false, isStale: false, isCached: false,
  description: 'Last close from Yahoo.', ...overrides,
})

const withTooltips = (ui: React.ReactNode) => render(<TooltipProvider>{ui}</TooltipProvider>)

describe('price provenance', () => {
  it('labels a last close with its date and never as live', () => {
    const { container } = render(<QuoteCell quote={quote()} />)
    expect(screen.getByText('$200.00')).toBeInTheDocument()
    expect(screen.getByText(/Last close · Oct 9/)).toBeInTheDocument()
    expect(container.textContent?.toLowerCase()).not.toContain('live')
  })

  it('marks simulated and stale prices', () => {
    render(<QuoteCell quote={quote({ isSimulated: true, isStale: true })} />)
    expect(screen.getByText(/SIMULATED/)).toBeInTheDocument()
    expect(screen.getByText(/stale/)).toBeInTheDocument()
  })

  it('shows a placeholder instead of a price when none is stored', () => {
    render(<QuoteCell quote={quote({ kind: 'None', price: null })} />)
    expect(screen.getByText('No price available')).toBeInTheDocument()
    expect(screen.queryByText(/0\.00/)).not.toBeInTheDocument() // missing is never shown as zero
  })

  it('shows the SIMULATED badge for mock data', () => {
    withTooltips(<QuoteProvenance quote={quote({ isSimulated: true, provider: 'Mock' })} />)
    expect(screen.getByText(/simulated/i)).toBeInTheDocument()
  })
})
