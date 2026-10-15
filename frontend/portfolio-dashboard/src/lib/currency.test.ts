import { describe, expect, it } from 'vitest'
import { exposureRows, foreignCurrencies, fxPhrase } from './currency'

describe('currency helpers', () => {
  it('states rates in the app convention, readable for small rates', () => {
    expect(fxPhrase(0.9132, 'USD', 'EUR')).toBe('1 USD = 0.9132 EUR')
    expect(fxPhrase(0.0061, 'JPY', 'EUR')).toBe('1 JPY = 0.0061 EUR · 1 EUR = 163.93 JPY')
    expect(fxPhrase(1, 'EUR', 'EUR')).toBe('same currency')
  })

  it('never shows an invalid rate as a number', () => {
    expect(fxPhrase(null, 'USD', 'EUR')).toBe('—')
    expect(fxPhrase(0, 'USD', 'EUR')).toBe('—')
    expect(fxPhrase(Number.NaN, 'USD', 'EUR')).toBe('—')
  })

  it('orders exposure by weight and drops empty rows', () => {
    const rows = exposureRows([
      { currency: 'EUR cash', weight: 0.1, holdings: 0 },
      { currency: 'USD', weight: 0.5, holdings: 2 },
      { currency: 'JPY', weight: 0, holdings: 0 },
      { currency: 'GBP', weight: 0.4, holdings: 1 },
    ])
    expect(rows.map((r) => r.currency)).toEqual(['USD', 'GBP', 'EUR cash'])
  })

  it('lists foreign currencies once', () => {
    expect(foreignCurrencies(['USD', 'EUR', 'USD', null, 'JPY'], 'EUR')).toEqual(['JPY', 'USD'])
  })
})
