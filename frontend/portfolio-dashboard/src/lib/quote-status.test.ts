import { describe, expect, it } from 'vitest'
import { age, quoteStatus, symbolsNeedingRefresh } from './quote-status'
import type { StockQuote } from './types'

const q = (o: Partial<StockQuote>): StockQuote => ({
  symbol: 'SAP.DE', price: 196.94, currency: 'EUR', kind: 'LastClose', priceDate: '2026-10-14', asOfUtc: '2026-10-14T15:30:00Z', previousClose: 197.28,
  previousCloseDate: null, change: -0.34, changePercent: -0.0017, provider: 'Yahoo', retrievedAtUtc: '2026-10-14T18:00:00Z', isSimulated: false,
  isStale: false, isCached: false, description: 'd', ...o,
})

describe('quote status', () => {
  const now = new Date('2026-10-14T18:05:00Z')
  it('never calls a price live; distinguishes delayed quotes from closes', () => {
    expect(quoteStatus(q({ kind: 'DelayedQuote', asOfUtc: '2026-10-14T17:50:00Z' }), now).label).toBe('Delayed quote')
    expect(quoteStatus(q({}), now)).toMatchObject({ label: 'Last close', when: 'Oct 14' })
  })
  it('reports missing prices as missing (not zero) and flags stale, cached, simulated', () => {
    expect(quoteStatus(q({ kind: 'None', price: null }), now).label).toBe('No price')
    expect(quoteStatus(q({ isStale: true, isCached: true, isSimulated: true }), now).flags).toEqual(['simulated', 'stale', 'cached'])
  })
  it('requests refreshes only for missing or cached prices', () => {
    expect(symbolsNeedingRefresh([q({ symbol: 'A' }), q({ symbol: 'B', isCached: true }), q({ symbol: 'C', kind: 'None', price: null })])).toEqual(['B', 'C'])
  })
  it('formats ages', () => {
    expect(age('2026-10-14T18:02:00Z', now)).toBe('3 min ago')
    expect(age(null, now)).toBe('never')
  })
})
