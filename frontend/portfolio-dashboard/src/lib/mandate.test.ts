import { describe, expect, it } from 'vitest'
import { fromForm, toForm } from '@/lib/mandate'
import type { PilotMandate } from '@/lib/types'

const mandate: PilotMandate = {
  universe: ['AAPL', 'MSFT'], indexes: ['SP500'], excludedSymbols: ['XOM'],
  maxPositionWeight: 0.25, minPositionWeight: 0.02, minHoldings: 5, maxHoldings: 20, minCashWeight: 0, maxCashWeight: 0.2,
  maxTurnover: 0.5, minTradeValue: 100, maxVolatility: null, drawdownPauseThreshold: 0.25,
  sectorLimits: { Energy: 0.1 }, currencyLimits: { EUR: 0.3 }, countryLimits: { US: 0.7 }, benchmark: null,
} as PilotMandate

describe('mandate form', () => {
  it('reads back exactly the mandate it was filled from', () => {
    const r = fromForm(toForm(mandate, 'Weekly', 'high'))
    expect('mandate' in r && r.mandate).toEqual(mandate)
  })

  it('keeps chosen stocks and limits as lists and maps – nothing is parsed from free text', () => {
    const f = toForm(mandate, 'Weekly', 'high')
    expect(f.universe).toEqual(['AAPL', 'MSFT'])
    expect(f.sectors).toEqual({ Energy: '10' })
    const r = fromForm({ ...f, countries: { US: '70', DE: '12.5' }, benchmark: '^GDAXI' })
    expect('mandate' in r && r.mandate.countryLimits).toEqual({ US: 0.7, DE: 0.125 })
    expect('mandate' in r && r.mandate.benchmark).toBe('^GDAXI')
  })

  it('names the field when a typed number is not a number', () => {
    const r = fromForm({ ...toForm(mandate, 'Weekly', 'high'), sectors: { Energy: 'ten' } })
    expect('error' in r && r.error).toBe('Sector limit for Energy must be a number (percent).')
    const empty = fromForm({ ...toForm(mandate, 'Weekly', 'high'), maxPos: '' })
    expect('error' in empty && empty.error).toBe('Maximum position must be a number (percent).')
  })
})

describe('mandate form percentages', () => {
  it('accepts a percent sign and a decimal comma, and refuses what is not one number', () => {
    const f = toForm(mandate, 'Weekly', 'high')
    const read = (maxPos: string) => fromForm({ ...f, maxPos })
    expect('mandate' in read('12,5 %') && read('12,5 %')).toMatchObject({ mandate: { maxPositionWeight: 0.125 } })
    expect('mandate' in read('20%%') && read('20%%')).toMatchObject({ mandate: { maxPositionWeight: 0.2 } })
    expect(read('1,2,3')).toEqual({ error: 'Maximum position must be a number (percent).' })
    expect(read('%')).toEqual({ error: 'Maximum position must be a number (percent).' })
  })
})
