import { describe, expect, it } from 'vitest'
import { dataNeeded, hybridPossible, strategyKey, strategyRef } from './strategies'
import type { PortfolioStrategy } from './types'

const strategy = (over: Partial<PortfolioStrategy>): PortfolioStrategy => ({
  kind: 'Mathematical', id: 'r1', label: 'Minimum Variance', detail: '', createdAtUtc: '2026-10-20T10:00:00Z', stocks: 2, currency: 'USD',
  usesSimulatedData: false, universe: ['AAPL', 'MSFT'], benchmarkSymbol: 'SPY', historyDaysBeforeStart: 388, ...over,
})

describe('portfolio strategies', () => {
  const math = strategy({})
  const math2 = strategy({ id: 'r2' })
  const ml = strategy({ kind: 'MachineLearning', id: 'e1', universe: ['MSFT', 'NVDA'], historyDaysBeforeStart: 2922 })

  it('keys and references distinguish runs from experiments', () => {
    expect(strategyKey(ml)).toBe('MachineLearning:e1')
    expect(strategyRef('Mathematical:r2', [math, math2, ml])).toEqual({ kind: 'Mathematical', id: 'r2' })
    expect(strategyRef('Mathematical:e1', [math, ml])).toBeNull()
  })

  it('offers the hybrid only for one mathematical and one ML optimization', () => {
    expect(hybridPossible(math, ml)).toBe(true)
    expect(hybridPossible(math, math2)).toBe(false)
    expect(hybridPossible(ml, undefined)).toBe(false)
  })

  it('checks the data of both strategies from the start minus the longest history', () => {
    const need = dataNeeded([math, ml], '2024-01-01', null, 'EUR')
    expect(need.symbols.sort()).toEqual(['AAPL', 'MSFT', 'NVDA'])
    expect(need.from).toBe('2016-01-01')                      // 2922 days = 8 years incl. two leap days
    expect(need).toMatchObject({ benchmarkSymbol: 'SPY', currency: 'EUR', to: null })
  })
})
