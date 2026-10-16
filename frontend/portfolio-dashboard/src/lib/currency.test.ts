import { describe, expect, it } from 'vitest'
import { converterCurrencies, convertAmount, crossFromBoard, exposureRows, foreignCurrencies, fxPhrase } from './currency'

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

  it('converts amounts only with valid numbers', () => {
    expect(convertAmount(100, 1.1)).toBeCloseTo(110, 10)
    expect(convertAmount(100, 0)).toBeNull()
    expect(convertAmount(Number.NaN, 1.1)).toBeNull()
    expect(convertAmount(100, null)).toBeNull()
  })

  it('derives cross rates from a board against one base', () => {
    const rates = { USD: 1.1, JPY: 160, GBP: 0.85 }
    expect(crossFromBoard(rates, 'EUR', 'EUR', 'USD')).toBeCloseTo(1.1, 12)
    expect(crossFromBoard(rates, 'EUR', 'USD', 'EUR')).toBeCloseTo(1 / 1.1, 12)
    expect(crossFromBoard(rates, 'EUR', 'USD', 'JPY')).toBeCloseTo(160 / 1.1, 10)
    expect(crossFromBoard(rates, 'EUR', 'USD', 'CHF')).toBeNull()
    expect(crossFromBoard(rates, 'EUR', 'GBP', 'GBP')).toBe(1)
  })

  it('offers the portfolio currency first, then supported majors', () => {
    expect(converterCurrencies('CHF', ['EUR', 'USD', 'CHF', 'JPY']).slice(0, 4)).toEqual(['CHF', 'USD', 'EUR', 'JPY'])
  })
})
