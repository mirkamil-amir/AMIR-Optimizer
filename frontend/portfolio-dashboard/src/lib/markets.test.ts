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
    // The twenty markets added later, including symbols with digits, series letters and '&'.
    expect(countryOfSymbol('005930.KS')).toBe('KR')
    expect(countryOfSymbol('600519.SS')).toBe('CN')
    expect(countryOfSymbol('PETR4.SA')).toBe('BR')
    expect(countryOfSymbol('PE&OLES.MX')).toBe('MX')
    expect(countryOfSymbol('NPN.JO')).toBe('ZA')
    expect(countryOfSymbol('ALPHA.AT')).toBe('GR')
    expect(countryOfSymbol('R-LQ45X.JK')).toBe('ID')
    expect(countryOfSymbol('^J200.JO')).toBe('ZA')
    expect(countryOfSymbol('^BVSP')).toBe('BR')
    expect(countryOfSymbol('OSEBX.OL')).toBe('NO')
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
    expect(venues).toHaveLength(36)
    for (const v of venues) {
      expect(v.lat).toBeGreaterThan(-58)
      expect(v.lat).toBeLessThan(84)
      expect(Math.abs(v.lon)).toBeLessThanOrEqual(180)
    }
  })
})
