import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { UniverseNav, type UniverseScope } from './universe-nav'
import type { Market, MarketIndex } from '@/lib/types'

const index = (code: string, name: string, country: string, members: number, isHistorical = false): MarketIndex => ({
  code, name, country, currency: 'EUR', benchmarkSymbol: null, isHistorical, description: null, members, membersWithPriceHistory: 0,
  membersWithQuotes: 0, source: 'test', sourceUrl: null, sourceRevision: null, licence: null,
} as MarketIndex)

const markets: Market[] = [
  { country: 'US', name: 'United States', indices: [index('SP500', 'S&P 500', 'US', 503)] },
  { country: 'DE', name: 'Germany', indices: [index('DAX40', 'DAX 40', 'DE', 40), index('DAX30', 'DAX 30 (historical)', 'DE', 30, true)] },
  { country: 'FR', name: 'France', indices: [index('CAC40', 'CAC 40', 'FR', 40)] },
  { country: 'ZA', name: 'South Africa', indices: [index('JSE40', 'FTSE/JSE Top 40', 'ZA', 39)] },
]

function renderNav(scope: UniverseScope = {}) {
  const onChange = vi.fn()
  render(<UniverseNav markets={markets} sectors={[]} scope={scope} onChange={onChange} loading={false} />)
  return onChange
}

describe('UniverseNav', () => {
  it('groups markets by region, folded, with counts that leave out historical lists', () => {
    renderNav()
    expect(screen.getByRole('button', { name: /^Europe/ })).toHaveTextContent('80')   // DAX 40 + CAC 40, not DAX 30
    expect(screen.getByRole('button', { name: /^All markets/ })).toHaveTextContent('622')
    expect(screen.queryByRole('button', { name: /^Germany/ })).toBeNull()
    expect(screen.getByRole('button', { name: /^Middle East & Africa/ })).toBeInTheDocument()
  })

  it('selects a whole region as several markets', async () => {
    const onChange = renderNav()
    await userEvent.click(screen.getByRole('button', { name: /^Europe/ }))
    expect(onChange).toHaveBeenCalledWith({ region: 'Europe', country: 'DE,FR' })
  })

  it('opens the region of the chosen market and lists its indices', () => {
    renderNav({ country: 'DE' })
    expect(screen.getByRole('button', { name: /^Germany/ })).toHaveAttribute('aria-current', 'true')
    expect(screen.getByRole('button', { name: /DAX 30 \(historical\)/ })).toBeInTheDocument()
    // A market with a single index has no second level.
    expect(screen.queryByRole('button', { name: /^CAC 40/ })).toBeNull()
  })

  it('folds and unfolds a region without changing the filter', async () => {
    const onChange = renderNav()
    await userEvent.click(screen.getByRole('button', { name: 'Show Americas' }))
    expect(screen.getByRole('button', { name: /^United States/ })).toBeInTheDocument()
    expect(onChange).not.toHaveBeenCalled()
  })
})
