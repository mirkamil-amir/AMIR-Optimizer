import { describe, expect, it } from 'vitest'
import { equalWeight, maximumReturnForRisk, maximumSharpe, minimumVariance, portfolio, sigmoid, targetReturn } from './worked-examples'
import { strategyGuides } from './strategy-guide'

describe('worked examples (two illustrative stocks)', () => {
  it('minimum variance matches the closed form and beats both single stocks on risk', () => {
    const gmv = minimumVariance()
    expect(gmv.weightA).toBeCloseTo(0.0780 / 0.1060, 6) // (σB² − ρσAσB) / (σA² + σB² − 2ρσAσB)
    expect(gmv.volatility).toBeLessThan(0.2)
    for (const w of [0, 0.25, 0.5, 0.9, 1]) expect(portfolio(w).volatility).toBeGreaterThanOrEqual(gmv.volatility - 1e-12)
  })

  it('maximum Sharpe has the highest Sharpe ratio of all long-only mixes', () => {
    const tangency = maximumSharpe()
    for (let w = 0; w <= 1; w += 0.01) expect(portfolio(w).sharpe).toBeLessThanOrEqual(tangency.sharpe + 1e-12)
    expect(tangency.weightA).toBeCloseTo(0.00342 / 0.00642, 6) // Σ⁻¹(μ − rf) normalized
  })

  it('target return binds only above the minimum-variance return', () => {
    expect(targetReturn(0.09)).toEqual(minimumVariance())
    expect(targetReturn(0.1).expectedReturn).toBeCloseTo(0.1, 12)
    expect(targetReturn(0.11).volatility).toBeGreaterThan(targetReturn(0.1).volatility)
  })

  it('max return for a volatility limit reaches the limit or reports infeasibility', () => {
    expect(maximumReturnForRisk(0.1)).toBeNull()
    expect(maximumReturnForRisk(0.2)!.volatility).toBeCloseTo(0.2, 6)
    expect(maximumReturnForRisk(0.5)!.weightB).toBe(1)
  })

  it('equal weight and the logistic function behave as described', () => {
    expect(equalWeight().weightA).toBe(0.5)
    expect(sigmoid(0)).toBe(0.5)
    expect(sigmoid(0.6)).toBeCloseTo(0.6457, 4)
  })
})

describe('strategy guide', () => {
  it('has the complete 12-part structure for every strategy', () => {
    expect(strategyGuides.map((g) => g.id)).toEqual(expect.arrayContaining([
      'minimum-variance', 'maximum-sharpe', 'target-return', 'maximum-return-for-risk', 'equal-weight',
      'ridge-regression', 'logistic-regression', 'random-forest', 'gradient-boosting', 'ml-portfolio-construction', 'strategy-comparison',
    ]))
    for (const g of strategyGuides) {
      expect(g.what.length, g.id).toBeGreaterThan(40)
      expect(g.theory.length, g.id).toBeGreaterThan(0)
      expect(g.objective.length, g.id).toBeGreaterThan(10)
      expect(g.math.length, g.id).toBeGreaterThan(0)
      expect(g.variables.length, g.id).toBeGreaterThan(0)
      expect(g.how.length, g.id).toBeGreaterThan(1)
      expect(g.inputs.length, g.id).toBeGreaterThan(0)
      expect(g.interpretation.length, g.id).toBeGreaterThan(0)
      expect(g.whenToUse.length, g.id).toBeGreaterThan(0)
      expect(g.advantages.length + g.limitations.length, g.id).toBeGreaterThan(2)
      expect(g.example.steps.length, g.id).toBeGreaterThan(0)
      expect(g.results.length, g.id).toBeGreaterThan(0)
      expect(g.caveats.length, g.id).toBeGreaterThan(0)
      for (const r of g.related) expect(strategyGuides.some((x) => x.id === r), `${g.id} → ${r}`).toBe(true)
    }
  })

  it('never claims live prices or guaranteed profits', () => {
    const text = JSON.stringify(strategyGuides).toLowerCase()
    expect(text).not.toMatch(/real-time price|guaranteed (profit|return)|will outperform/)
  })
})
