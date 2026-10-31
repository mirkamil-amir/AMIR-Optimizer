import { describe, expect, it, vi } from 'vitest'
import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HoldingsTable } from './holdings-table'
import type { Holding, HoldingsResponse } from '@/lib/types'

vi.mock('@/lib/queries', () => ({
  usePrices: () => ({ data: undefined, isLoading: false }),
  useRisk: () => ({ data: undefined }),
  useTransactions: () => ({ data: undefined }),
}))

const base: Holding = {
  assetId: 1, symbol: '7203.T', name: 'Toyota Motor', sector: 'Consumer', quantity: 100, averageCost: 12, costBasis: 1200, latestPrice: 2000,
  priceDate: '2026-10-30', priceIsStale: false, marketValue: 1240, weight: 0.6, unrealizedPnl: 40, unrealizedReturn: 0.033, realizedPnl: 0,
  targetWeight: 0.6, weightDeviation: 0, dailyChange: null, updatedAtUtc: '2026-10-30T18:00:00Z', currency: 'JPY', fxRate: 0.0062, fxRateDate: '2026-10-30', valuationProblem: null,
}
const data = { holdings: [base, { ...base, assetId: 2, symbol: 'SAP.DE', name: 'SAP', currency: 'EUR', fxRate: null, latestPrice: 230, priceIsStale: true }] } as unknown as HoldingsResponse

describe('HoldingsTable', () => {
  it('shows the price in the portfolio currency with the rate, the original price with its date, and opens in full screen', async () => {
    render(<HoldingsTable data={data} loading={false} currency="EUR" account="EUR" portfolioId="p" />)
    expect(screen.getByRole('columnheader', { name: /Price \(EUR\)/ })).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: /Original price/ })).toBeInTheDocument()
    const toyota = screen.getByRole('row', { name: /7203\.T/ })
    expect(toyota).toHaveTextContent(/1 EUR = [\d,.]+ JPY/)
    expect(toyota).toHaveTextContent('¥')
    expect(screen.getByRole('row', { name: /SAP\.DE/ })).toHaveTextContent('stale')
    expect(within(screen.getAllByRole('rowgroup')[0]).getAllByRole('row')[0].parentElement).toHaveClass('sticky')
    await userEvent.click(screen.getByRole('button', { name: 'Full screen' }))
    const dialog = await screen.findByRole('dialog', { name: 'Holdings' })
    expect(within(dialog).getByRole('combobox', { name: 'Filter by sector' })).toBeInTheDocument()
    expect(within(dialog).getByRole('row', { name: /7203\.T/ })).toBeInTheDocument()
  })
})
