import { describe, expect, it } from 'vitest'
import { drawdownFromCumulative, equalWeight, weightsBySector } from './run-charts'
import type { Allocation } from './types'

const a = (symbol: string, weight: number, sector: string | null): Allocation =>
  ({ symbol, name: null, sector, weight, expectedReturn: 0, volatility: 0, riskContribution: null })

describe('run charts', () => {
  it('measures drawdowns from the running peak of the cumulative return', () => {
    const dd = drawdownFromCumulative([0, 0.1, -0.12, 0.21])
    expect(dd[0]).toBe(0)
    expect(dd[1]).toBe(0)
    expect(dd[2]).toBeCloseTo(0.88 / 1.1 - 1, 12) // −20 % below the peak of +10 %
    expect(dd[3]).toBe(0)
  })

  it('sums weights per sector, largest first, and skips zero weights', () => {
    expect(weightsBySector([a('AAPL', 0.3, 'Tech'), a('MSFT', 0.2, 'Tech'), a('XOM', 0.4, 'Energy'), a('X', 0.1, null), a('Y', 0, 'Utilities')]))
      .toEqual([{ sector: 'Tech', weight: 0.5 }, { sector: 'Energy', weight: 0.4 }, { sector: 'Unclassified', weight: 0.1 }])
  })

  it('spreads the invested share evenly over the universe', () => {
    expect(equalWeight(4, 0.99)).toBeCloseTo(0.2475, 12)
    expect(equalWeight(0, 1)).toBe(0)
  })
})
