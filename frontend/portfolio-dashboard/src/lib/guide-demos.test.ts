import { describe, expect, it } from 'vitest'
import {
  constructWeights, equalWeightVolatility, fitBoosting, fitForest, fitTree, gmvMix, growthAfterCosts, maxReturnForRiskMix, meanSquaredError,
  mix, predictBoosting, predictForest, predictTree, ridgeShrink, sampleMonthlyReturns, samplePoints, sampleScores, sampleTwoAssets, seededRandom,
  tangencyMix, targetReturnMix, truePattern,
} from './guide-demos'
import { maximumReturnForRisk, maximumSharpe, minimumVariance, targetReturn } from './worked-examples'

const p = sampleTwoAssets

describe('two-stock figures', () => {
  it('reproduce the worked examples at the starting values', () => {
    expect(gmvMix(p).wA).toBeCloseTo(minimumVariance().weightA, 12)
    expect(tangencyMix(p).wA).toBeCloseTo(maximumSharpe().weightA, 12)
    expect(targetReturnMix(p, 0.11)!.wA).toBeCloseTo(targetReturn(0.11).weightA, 12)
    expect(maxReturnForRiskMix(p, 0.22)!.vol).toBeCloseTo(maximumReturnForRisk(0.22)!.volatility, 9)
  })

  it('lowers the minimum volatility as correlation falls and reaches zero at −1 for equal volatilities', () => {
    expect(gmvMix({ ...p, rho: -0.5 }).vol).toBeLessThan(gmvMix({ ...p, rho: 0.5 }).vol)
    expect(gmvMix({ ...p, volB: p.volA, rho: -1 }).vol).toBeCloseTo(0, 9)
  })

  it('moves the tangency portfolio toward Stock B when its expected return rises', () => {
    expect(tangencyMix({ ...p, muB: 0.16 }).wA).toBeLessThan(tangencyMix({ ...p, muB: 0.1 }).wA)
  })

  it('reports unreachable targets and limits instead of inventing a portfolio', () => {
    expect(targetReturnMix(p, 0.2)).toBeNull()
    expect(maxReturnForRiskMix(p, 0.05)).toBeNull()
    expect(maxReturnForRiskMix(p, 0.5)!.wA).toBe(0)
    expect(mix(p, 1).vol).toBeCloseTo(p.volA, 12)
  })
})

describe('equal weight', () => {
  it('approaches √ρ·σ with many stocks and equals σ for one', () => {
    expect(equalWeightVolatility(1, 0.3, 0.25)).toBeCloseTo(0.3, 12)
    expect(equalWeightVolatility(10_000, 0.3, 0.25)).toBeCloseTo(0.15, 3)
  })
})

describe('ML portfolio construction', () => {
  it('matches the guide: rank-linear 3:2:1 with a 40% cap gives 40/40/20', () => {
    const { weights, cash } = constructWeights(sampleScores.slice(0, 5), 3, 'rank', 0.4)
    expect(weights.A).toBeCloseTo(0.4, 12)
    expect(weights.B).toBeCloseTo(0.4, 12)
    expect(weights.C).toBeCloseTo(0.2, 12)
    expect(cash).toBeCloseTo(0, 12)
  })

  it('keeps what nobody can take in cash and never exceeds the cap', () => {
    const { weights, cash } = constructWeights(sampleScores, 2, 'equal', 0.3)
    expect(Object.values(weights)).toEqual([0.3, 0.3])
    expect(cash).toBeCloseTo(0.4, 12)
    const scored = constructWeights(sampleScores, 5, 'score', 0.35)
    expect(Math.max(...Object.values(scored.weights))).toBeLessThanOrEqual(0.35 + 1e-12)
    expect(Object.values(scored.weights).reduce((a, b) => a + b, 0) + scored.cash).toBeCloseTo(1, 12)
  })
})

describe('regression demos', () => {
  it('shrinks ridge coefficients toward zero and leaves them unchanged at λ = 0', () => {
    expect(ridgeShrink(0.4, 0)).toBe(0.4)
    expect(ridgeShrink(0.4, 1)).toBeCloseTo(0.2, 12)
  })
})

describe('trees on seeded sample data', () => {
  const train = samplePoints(80, 11)
  const test = samplePoints(80, 23)

  it('generates the same sample data for the same seed', () => {
    expect(samplePoints(5, 11)).toEqual(samplePoints(5, 11))
    expect(seededRandom(1)()).toBe(seededRandom(1)())
  })

  it('fits a tree that beats predicting the mean on its training data', () => {
    const mean = train.reduce((s, q) => s + q.y, 0) / train.length
    const tree = fitTree(train, 3)
    expect(meanSquaredError(train, (x) => predictTree(tree, x))).toBeLessThan(meanSquaredError(train, () => mean))
  })

  it('averages many bootstrap trees into a prediction closer to the hidden pattern than one tree', () => {
    const forest = fitForest(train, 100, 4, 5)
    const grid = Array.from({ length: 50 }, (_, i) => ({ x: i / 49, y: truePattern(i / 49) }))
    expect(meanSquaredError(grid, (x) => predictForest(forest, 100, x))).toBeLessThan(meanSquaredError(grid, (x) => predictForest(forest, 1, x)))
  })

  it('boosting lowers the training error step by step', () => {
    const model = fitBoosting(train, 60, 0.3, 2)
    const err = (m: number) => meanSquaredError(train, (x) => predictBoosting(model, m, 0.3, x))
    expect(err(60)).toBeLessThan(err(10))
    expect(err(10)).toBeLessThan(err(0))
    expect(meanSquaredError(test, (x) => predictBoosting(model, 0, 0.3, x))).toBeGreaterThan(0)
  })
})

describe('costs', () => {
  it('ends lower with more turnover or higher costs and equals gross growth without costs', () => {
    const gross = sampleMonthlyReturns()
    const free = growthAfterCosts(gross, 0.8, 0)
    expect(growthAfterCosts(gross, 0.1, 0).at(-1)).toBeCloseTo(free.at(-1)!, 9)
    expect(growthAfterCosts(gross, 0.8, 50).at(-1)!).toBeLessThan(growthAfterCosts(gross, 0.1, 50).at(-1)!)
    expect(free).toHaveLength(61)
  })
})
