import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { UniverseNav } from './universe-nav'
import { describeScope, emptyScope, marketCount, scopeParts, type UniverseScope } from '@/lib/universe-scope'
import type { Market, MarketIndex, Sector } from '@/lib/types'

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

const sectors: Sector[] = [
  { name: 'Energy', stockCount: 30, withPriceData: 10 },
  { name: 'Utilities', stockCount: 20, withPriceData: 5 },
]

const scopeOf = (s: Partial<UniverseScope>): UniverseScope => ({ ...emptyScope, ...s })

function renderNav(scope: UniverseScope = emptyScope) {
  const onChange = vi.fn()
  render(<UniverseNav markets={markets} sectors={sectors} scope={scope} onChange={onChange} loading={false} />)
  return onChange
}

describe('UniverseNav', () => {
  it('groups markets by region, folded, with counts that leave out historical lists', () => {
    renderNav()
    expect(screen.getByText('Europe').closest('label')).toHaveTextContent('80')   // DAX 40 + CAC 40, not DAX 30
    expect(screen.getByText(/All markets · 622 stocks/)).toBeInTheDocument()
    expect(screen.queryByRole('checkbox', { name: 'Germany' })).toBeNull()
    expect(screen.getByRole('checkbox', { name: 'Middle East & Africa' })).toBeInTheDocument()
  })

  it('counts each stock of a market once when the server sends instrument counts', () => {
    const counted = markets.map((m) => ({ ...m, instruments: m.country === 'DE' ? 44 : m.country === 'US' ? 504 : marketCount(m) }))
    render(<UniverseNav markets={counted} sectors={[]} scope={emptyScope} onChange={vi.fn()} loading={false} />)
    expect(screen.getByText('Europe').closest('label')).toHaveTextContent('84')
  })

  it('ticks a whole region as all its markets, added to what is already chosen', async () => {
    const onChange = renderNav(scopeOf({ countries: ['US'], indices: ['DAX40'] }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'Europe' }))
    // The DAX 40 is part of Germany now, so it is not listed separately.
    expect(onChange).toHaveBeenCalledWith(scopeOf({ countries: ['US', 'DE', 'FR'], indices: [] }))
  })

  it('shows a partly chosen region as mixed and unticks a fully chosen one', async () => {
    const onChange = renderNav(scopeOf({ countries: ['DE', 'FR', 'US'] }))
    expect(screen.getByRole('checkbox', { name: 'Europe' })).toBeChecked()
    await userEvent.click(screen.getByRole('checkbox', { name: 'Europe' }))
    expect(onChange).toHaveBeenCalledWith(scopeOf({ countries: ['US'] }))
  })

  it('ticks several markets and indices of different regions', async () => {
    const onChange = renderNav(scopeOf({ countries: ['US'] }))
    await userEvent.click(screen.getByRole('button', { name: 'Show Europe' }))
    expect(screen.getByRole('checkbox', { name: 'Europe' })).toHaveAttribute('aria-checked', 'false')
    await userEvent.click(screen.getByRole('checkbox', { name: 'France' }))
    expect(onChange).toHaveBeenLastCalledWith(scopeOf({ countries: ['US', 'FR'] }))
    await userEvent.click(screen.getByRole('button', { name: 'Show Germany indices' }))
    await userEvent.click(screen.getByRole('checkbox', { name: 'DAX 40' }))
    expect(onChange).toHaveBeenLastCalledWith(scopeOf({ countries: ['US'], indices: ['DAX40'] }))
  })

  it('marks a region with only an index chosen as mixed, and an index of a chosen market as included', async () => {
    renderNav(scopeOf({ indices: ['DAX40'] }))
    expect(screen.getByRole('checkbox', { name: 'Europe' })).toHaveAttribute('aria-checked', 'mixed')
    expect(screen.getByRole('checkbox', { name: 'Germany' })).toHaveAttribute('aria-checked', 'mixed')
  })

  it('finds a market or index by name and opens its region', async () => {
    renderNav()
    await userEvent.type(screen.getByRole('textbox', { name: 'Find a market or index' }), 'cac')
    expect(screen.getByRole('checkbox', { name: 'France' })).toBeInTheDocument()
    expect(screen.queryByRole('checkbox', { name: 'Americas' })).toBeNull()
  })

  it('ticks several sectors', async () => {
    const onChange = renderNav(scopeOf({ sectors: ['Energy'] }))
    expect(screen.getByRole('checkbox', { name: 'Energy' })).toBeChecked()
    await userEvent.click(screen.getByRole('checkbox', { name: 'Utilities' }))
    expect(onChange).toHaveBeenCalledWith(scopeOf({ sectors: ['Energy', 'Utilities'] }))
  })
})

describe('scope labels', () => {
  it('names whole regions once, then single markets, indices and sectors', () => {
    const scope = scopeOf({ countries: ['DE', 'FR', 'US'], indices: ['JSE40'], sectors: ['Energy'] })
    expect(scopeParts(markets, scope).map((p) => p.label)).toEqual(['Europe', 'United States', 'FTSE/JSE Top 40', 'Energy'])
    expect(describeScope(markets, scope)).toBe('Europe, United States, FTSE/JSE Top 40 › Energy')
    expect(describeScope(markets, emptyScope)).toBe('All markets')
    // Removing the region chip removes exactly its markets.
    expect(scopeParts(markets, scope)[0].remove.countries).toEqual(['US'])
  })
})
