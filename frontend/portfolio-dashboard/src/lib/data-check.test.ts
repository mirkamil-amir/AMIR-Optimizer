import { describe, expect, it } from 'vitest'
import { lookbackStart, nextStep, shouldAsk } from './data-check'
import type { DataCheck } from './types'

const check = (over: Partial<DataCheck>): DataCheck => ({
  currency: 'USD', from: '2018-10-24', to: '2026-10-24', complete: false, downloadable: false, downloading: false, prices: [], rates: [], notes: [], ...over,
})

describe('missing-data check', () => {
  it('asks only when something can be downloaded or is arriving', () => {
    expect(shouldAsk(check({ complete: true }))).toBe(false)
    expect(shouldAsk(check({ downloadable: true }))).toBe(true)
    expect(shouldAsk(check({ downloading: true }))).toBe(true)
    // Only data the source does not have: no question, the analysis explains it.
    expect(shouldAsk(check({}))).toBe(false)
  })

  it('proceeds when complete, waits while downloads run, otherwise reports what is missing', () => {
    expect(nextStep(check({ complete: true }), false)).toBe('proceed')
    expect(nextStep(check({}), false)).toBe('wait')
    expect(nextStep(check({ downloading: true }), true)).toBe('wait')
    expect(nextStep(check({ downloadable: true }), true)).toBe('incomplete')
  })

  it('starts the lookback like the backtest loads it (1.5 calendar days per trading day + 10)', () => {
    expect(lookbackStart(new Date('2026-10-24T12:00:00Z'), 252)).toBe('2025-10-01')
  })
})
