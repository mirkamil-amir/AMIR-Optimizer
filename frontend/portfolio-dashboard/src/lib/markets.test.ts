import { describe, expect, it } from 'vitest'
import { countriesOf, countryOfSymbol, unresolvedSymbols, venues } from './markets'

describe('markets', () => {
  it('finds the market of a symbol from its venue suffix or index symbol', () => {
    expect(countryOfSymbol('AAPL')).toBe('US')
    expect(countryOfSymbol('BRK-B')).toBe('US')
    expect(countryOfSymbol('SAP.DE')).toBe('DE')
    expect(countryOfSymbol('NOVN.SW')).toBe('CH')
    expect(countryOfSymbol('0005.HK')).toBe('HK')
    expect(countryOfSymbol('M&M.NS')).toBe('IN')
    expect(countryOfSymbol('rci-b.to')).toBe('CA')
    expect(countryOfSymbol('^HSI')).toBe('HK')
    expect(countryOfSymbol('FTSEMIB.MI')).toBe('IT')
  })

  it('never guesses a market for an unknown venue or index', () => {
    expect(countryOfSymbol('ABC.XX')).toBeNull()
    expect(countryOfSymbol('^UNKNOWN')).toBeNull()
    expect(unresolvedSymbols.has('ABC.XX')).toBe(true)
    expect(countriesOf(['ABC.XX', 'SAP.DE', 'SAP.DE', 'MC.PA'])).toEqual(['DE', 'FR'])
  })

  it('lists distinct markets in venue order', () => {
    expect(countriesOf(['D05.SI', 'MSFT', 'AAPL', 'SAP.DE', 'BHP.AX'])).toEqual(['US', 'DE', 'AU', 'SG'])
    expect(countriesOf([])).toEqual([])
  })

  it('has one exchange city with valid coordinates per market', () => {
    expect(new Set(venues.map((v) => v.country)).size).toBe(venues.length)
    expect(venues).toHaveLength(16)
    for (const v of venues) {
      expect(v.lat).toBeGreaterThan(-58)
      expect(v.lat).toBeLessThan(84)
      expect(Math.abs(v.lon)).toBeLessThanOrEqual(180)
    }
  })
})
