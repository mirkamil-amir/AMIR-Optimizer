import { describe, expect, it } from 'vitest'
import { interpretMlExperiment, interpretOptimization } from './interpretation'
import type { MlExperiment, OptimizationRun } from './types'

const run = (overrides: Partial<OptimizationRun> = {}): OptimizationRun => ({
  id: 'r1', portfolioId: 'p1', createdAtUtc: '2026-10-14T10:00:00Z', objective: 'MinimumVariance', status: 'Succeeded', message: 'ok',
  expectedReturn: 0.09, volatility: 0.14, sharpeRatio: 0.36, periodStart: '2023-10-14', periodEnd: '2026-10-13', priceSource: 'Yahoo',
  usesSimulatedData: false, symbols: ['AAA', 'BBB', 'CCC', 'DDD'], observations: 750, dataSnapshotHash: 'x', currency: 'USD', fxSource: null, currencyExposure: null,
  settings: {
    universe: ['AAA', 'BBB', 'CCC', 'DDD'], lookbackDays: 756, objective: 'MinimumVariance', covarianceMethod: 'LedoitWolf', riskFreeRate: 0.03,
    benchmarkSymbol: 'SPY', minWeight: 0, maxWeight: 0.4, maxHoldings: null, minPositionWeight: null, cashReserve: 0, maxTurnover: null,
    sectorLimits: {}, excludedSymbols: [], targetReturn: null, maxVolatility: null,
  } as unknown as OptimizationRun['settings'],
  solverIterations: 10, solverCalls: 1, durationMs: 5, diagnostics: [], methodology: '',
  allocation: [
    { symbol: 'AAA', name: null, sector: 'Utilities', weight: 0.4, expectedReturn: 0.05, volatility: 0.12, riskContribution: 0.3 },
    { symbol: 'BBB', name: null, sector: 'Utilities', weight: 0.3, expectedReturn: 0.06, volatility: 0.15, riskContribution: 0.3 },
    { symbol: 'CCC', name: null, sector: 'Energy', weight: 0.3, expectedReturn: 0.12, volatility: 0.3, riskContribution: 0.4 },
    { symbol: 'DDD', name: null, sector: 'Energy', weight: 0, expectedReturn: 0.2, volatility: 0.45, riskContribution: 0 },
  ],
  analytics: {
    frontier: [], correlationSymbols: [], correlation: [], assetStatistics: [], inSample: null, covarianceShrinkage: 0.2, excludedForData: [],
    references: [
      { name: 'Equal weight', expectedReturn: 0.1, volatility: 0.2, sharpeRatio: 0.35, note: null },
      { name: 'Benchmark (SPY)', expectedReturn: 0.1, volatility: 0.17, sharpeRatio: 0.41, note: null },
    ],
  },
  ...overrides,
})

describe('interpretOptimization', () => {
  it('describes holdings, binding caps, concentration, risk drivers and caveats from the run numbers', () => {
    const insights = interpretOptimization(run())
    const text = insights.map((i) => i.text).join('\n')
    expect(text).toContain('holds 3 of 4')
    expect(text).toContain('AAA sits at the 40.0% maximum weight')
    expect(text).toContain('70.0% of the portfolio is in Utilities')
    expect(text).toContain('CCC contributes 40.0%')
    expect(text).toContain('benchmark (SPY) had 17.0% volatility')
    expect(text).toContain('Minimum variance only looks at risk')
    expect(insights.some((i) => i.kind === 'caution')).toBe(true)
    expect(insights.find((i) => i.text.includes('Estimated from'))?.kind).toBe('estimate')
  })

  it('explains infeasible runs instead of inventing weights', () => {
    const insights = interpretOptimization(run({ status: 'Infeasible', message: 'Max weight too low.', allocation: [] }))
    expect(insights[0].text).toContain('Max weight too low.')
    expect(insights.some((i) => i.text.includes('Relax the tightest'))).toBe(true)
  })

  it('flags simulated data', () => {
    expect(interpretOptimization(run({ usesSimulatedData: true })).some((i) => i.text.includes('SIMULATED'))).toBe(true)
  })
})

describe('interpretMlExperiment', () => {
  const ranking = (ic: number, t: number) => ({
    datesEvaluated: 40, topK: 5, meanInformationCoefficient: ic, informationCoefficientStdDev: 0.3, informationCoefficientTStat: t, nonOverlappingDates: 8,
    topKMeanOutcome: 0, universeMeanOutcome: 0, topKExcessOverUniverse: 0, topKHitRate: 0.5, topMinusBottomSpread: 0, rankStability: 0.9,
  })
  const experiment = (ic: number, t: number, trainIc = 0.05): MlExperiment => ({
    summary: { id: 'e1', status: 'Succeeded', error: null } as MlExperiment['summary'],
    configuration: { horizonDays: 21, weighting: 'EqualWeight', maxWeight: 0.2, modelFamily: 'RidgeRegression' } as MlExperiment['configuration'],
    symbols: ['A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J'],
    results: {
      testPeriod: { start: '2025-12-02', end: '2026-09-14', samples: 400, decisionDates: 40, purged: 0 },
      testMetrics: { period: 'Test', regression: { count: 400, mae: 0.06, rmse: 0.08, outOfSampleR2: -0.03, pooledRankCorrelation: 0, directionalAccuracy: 0.5 }, classification: null, ranking: ranking(ic, t) },
      trainMetrics: { period: 'Train', regression: null, classification: null, ranking: ranking(trainIc, 5) },
      baselines: [{ name: '12-1 momentum rank', description: '', regression: null, classification: null, ranking: ranking(0.026, 1.1) }],
    } as unknown as MlExperiment['results'],
    latestPredictions: null,
  } as unknown as MlExperiment)

  it('says plainly when there is no evidence of skill', () => {
    const text = interpretMlExperiment(experiment(-0.047, -0.57)).map((i) => i.text).join('\n')
    expect(text).toContain('not distinguishable from zero')
    expect(text).toContain('did not beat this baseline')
    expect(text).toContain('training-period average would have been at least as accurate')
    expect(text).toContain('Prediction accuracy is not investment performance')
  })

  it('warns about overfitting when training skill far exceeds test skill', () => {
    const text = interpretMlExperiment(experiment(0.01, 0.2, 0.4)).map((i) => i.text).join('\n')
    expect(text).toContain('overfitting')
  })
})
