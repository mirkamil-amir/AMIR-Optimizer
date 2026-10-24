import { pct } from './format'
import type { MlExperiment, MlBacktest, OptimizationRun, StrategyComparison, StrategyPerformance } from './types'

/**
 * Result interpretations, generated only from numbers the system actually calculated. Every statement is tagged so
 * students can tell what is a calculated fact, what is a model estimate (uncertain) and what is an educational
 * reading of the numbers. Nothing here invents a reason the calculation cannot support.
 */
export type InsightKind = 'fact' | 'estimate' | 'interpretation' | 'caution'

export interface Insight {
  kind: InsightKind
  text: string
}

const share = (x: number) => pct(x, 1)

// ------------------------------------------------------------------------------------------------ optimization

export function interpretOptimization(run: OptimizationRun): Insight[] {
  const out: Insight[] = []
  if (run.status !== 'Succeeded') {
    out.push({ kind: 'fact', text: `The optimization did not produce a portfolio: ${run.message}` })
    if (run.status === 'Infeasible')
      out.push({ kind: 'interpretation', text: 'No allocation can satisfy all constraints at once. Relax the tightest one (often the maximum weight, the sector caps or the target return) and run again.' })
    return out
  }

  const held = [...run.allocation].filter((a) => a.weight > 1e-6).sort((a, b) => b.weight - a.weight)
  const invested = held.reduce((s, a) => s + a.weight, 0)
  const top = held.slice(0, 3)
  out.push({
    kind: 'fact',
    text: `The portfolio holds ${held.length} of ${run.symbols.length} eligible stocks; ${share(invested)} is invested and ${share(1 - invested)} stays in cash. ` +
      `Largest positions: ${top.map((a) => `${a.symbol} ${share(a.weight)}`).join(', ')}.`,
  })

  const atCap = held.filter((a) => Math.abs(a.weight - run.settings.maxWeight) < 5e-4)
  if (atCap.length > 0)
    out.push({ kind: 'fact', text: `${atCap.map((a) => a.symbol).join(', ')} ${atCap.length === 1 ? 'sits' : 'sit'} at the ${share(run.settings.maxWeight)} maximum weight: the constraint, not only the objective, shaped these positions.` })

  // Sector concentration from the stored sector of each holding.
  const bySector = new Map<string, number>()
  for (const a of held) bySector.set(a.sector ?? 'Unclassified', (bySector.get(a.sector ?? 'Unclassified') ?? 0) + a.weight)
  const [topSector, topSectorWeight] = [...bySector.entries()].sort((a, b) => b[1] - a[1])[0] ?? ['', 0]
  if (invested > 0 && topSectorWeight / invested >= 0.5)
    out.push({ kind: 'interpretation', text: `${share(topSectorWeight)} of the portfolio is in ${topSector}. That is concentrated: one sector-wide shock would hit most of the portfolio.` })
  else if (bySector.size > 1)
    out.push({ kind: 'fact', text: `The weights spread over ${bySector.size} sectors; the largest is ${topSector} with ${share(topSectorWeight)}.` })

  // Why these weights, in terms of the objective that was actually solved.
  const byRisk = held.filter((a) => a.riskContribution !== null).sort((a, b) => (b.riskContribution ?? 0) - (a.riskContribution ?? 0))
  switch (run.objective) {
    case 'MinimumVariance': {
      const calm = [...held].sort((a, b) => a.volatility - b.volatility)[0]
      out.push({ kind: 'interpretation', text: `Minimum variance only looks at risk (volatility and correlation), not at expected returns. Low-volatility, weakly correlated stocks get large weights – here the least volatile holding is ${calm.symbol} (${share(calm.volatility)} a year). A higher-return stock may get little or no weight.` })
      break
    }
    case 'MaximumSharpe':
      out.push({ kind: 'interpretation', text: `Maximum Sharpe rewards high historical return per unit of risk. Stocks with strong past returns relative to their volatility get large weights – which makes this objective the most sensitive to noisy return estimates.` })
      break
    case 'TargetReturn':
      out.push({ kind: 'interpretation', text: `Target return finds the lowest-risk mix whose estimated return reaches ${run.settings.targetReturn !== null ? share(run.settings.targetReturn) : 'the target'}. Raising the target moves the portfolio up the efficient frontier: more estimated return, more risk.` })
      break
    case 'MaximumReturnForRisk':
      out.push({ kind: 'interpretation', text: `This objective searches the efficient frontier for the highest estimated return whose volatility stays within ${run.settings.maxVolatility !== null ? share(run.settings.maxVolatility) : 'the limit'}.` })
      break
    case 'EqualWeight':
      out.push({ kind: 'interpretation', text: 'Equal weight ignores estimates entirely: every eligible stock gets the same share. It is the simple baseline that optimized portfolios must beat to justify their complexity.' })
      break
  }
  if (byRisk.length > 1) {
    const r = byRisk[0]
    const weightShare = invested > 0 ? r.weight / invested : 0
    out.push({
      kind: 'fact',
      text: `${r.symbol} contributes ${share(r.riskContribution ?? 0)} of the portfolio's estimated variance with ${share(weightShare)} of the invested capital` +
        `${(r.riskContribution ?? 0) > weightShare * 1.2 ? ' – its risk share exceeds its capital share.' : '.'}`,
    })
  }

  if (run.expectedReturn !== null && run.volatility !== null)
    out.push({
      kind: 'estimate',
      text: `Estimated from ${run.observations} daily returns (${run.periodStart} to ${run.periodEnd}): return ${share(run.expectedReturn)} a year, volatility ${share(run.volatility)}` +
        `${run.sharpeRatio !== null ? `, Sharpe ratio ${run.sharpeRatio.toFixed(2)} with a ${share(run.settings.riskFreeRate)} risk-free rate` : ''}. These are historical averages, not forecasts.`,
    })

  const equal = run.analytics.references.find((r) => r.name.toLowerCase().startsWith('equal'))
  const bench = run.analytics.references.find((r) => r.name.toLowerCase().startsWith('benchmark'))
  if (run.volatility !== null && bench?.volatility != null)
    out.push({
      kind: 'fact',
      text: `In the same estimation window the benchmark ${bench.name.replace(/^Benchmark /, '')} had ${share(bench.volatility)} volatility – the portfolio's is ${run.volatility < bench.volatility ? 'lower' : 'higher'} (${share(run.volatility)}).`,
    })
  if (run.sharpeRatio !== null && equal?.sharpeRatio != null && run.objective !== 'EqualWeight')
    out.push({
      kind: 'interpretation',
      text: run.sharpeRatio > equal.sharpeRatio
        ? `In-sample, the optimized portfolio's Sharpe ratio (${run.sharpeRatio.toFixed(2)}) beats equal weight (${equal.sharpeRatio.toFixed(2)}). In-sample means "on the data used to choose the weights" – this advantage usually shrinks out of sample. Use a backtest or strategy comparison to check.`
        : `Even in-sample, equal weight has a Sharpe ratio (${equal.sharpeRatio.toFixed(2)}) at least as high as this portfolio (${run.sharpeRatio.toFixed(2)}). Optimization does not automatically improve risk-adjusted return.`,
    })

  if (run.usesSimulatedData) out.push({ kind: 'caution', text: 'These results use SIMULATED prices – they say nothing about real stocks.' })
  out.push({ kind: 'caution', text: 'Expected returns estimated from a few years of history are very noisy; small changes in the window can change the weights a lot. Past performance does not predict future results.' })
  return out
}

// ------------------------------------------------------------------------------------------------ machine learning

export function interpretMlExperiment(e: MlExperiment, backtest?: MlBacktest): Insight[] {
  const out: Insight[] = []
  const r = e.results
  if (!r) {
    if (e.summary.status === 'Failed') out.push({ kind: 'fact', text: `The experiment failed: ${e.summary.error ?? 'see the details'}. No metrics were produced – nothing was filled in.` })
    return out
  }
  const t = r.testMetrics
  const rank = t.ranking
  out.push({
    kind: 'fact',
    text: `The model was chosen on the validation period and evaluated once on the untouched test period ${r.testPeriod.start} – ${r.testPeriod.end} ` +
      `(${r.testPeriod.decisionDates} decision dates, only ${rank.nonOverlappingDates} of them with non-overlapping ${e.configuration.horizonDays}-day outcomes).`,
  })

  if (rank.meanInformationCoefficient !== null) {
    const ic = rank.meanInformationCoefficient
    const t_ = rank.informationCoefficientTStat
    const significant = t_ !== null && Math.abs(t_) >= 2
    out.push({
      kind: 'estimate',
      text: `Ranking skill (information coefficient, the rank correlation between predictions and what actually happened): ${ic.toFixed(3)}` +
        `${t_ !== null ? `, t-statistic ${t_.toFixed(2)}` : ''}. ${significant ? 'That is statistically distinguishable from zero on this sample.' : 'That is not distinguishable from zero – no evidence the model ranks stocks better than chance.'}`,
    })
    const momentum = r.baselines.find((b) => b.ranking?.meanInformationCoefficient != null)
    if (momentum?.ranking?.meanInformationCoefficient != null)
      out.push({
        kind: 'interpretation',
        text: `A simple baseline (${momentum.name}) scored an IC of ${momentum.ranking.meanInformationCoefficient.toFixed(3)} on the same dates. ${ic > momentum.ranking.meanInformationCoefficient ? 'The model ranked better than this baseline here' : 'The model did not beat this baseline'} – a model must beat simple rules to be worth its complexity.`,
      })
  }
  if (t.regression?.outOfSampleR2 != null)
    out.push({
      kind: 'estimate',
      text: `Out-of-sample R² ${t.regression.outOfSampleR2.toFixed(3)}: ${t.regression.outOfSampleR2 > 0 ? 'the predictions were slightly closer to the outcomes than the training average.' : 'predicting the training-period average would have been at least as accurate – typical for stock returns, which are mostly noise.'}`,
    })
  if (t.classification?.rocAuc != null)
    out.push({
      kind: 'estimate',
      text: `ROC-AUC ${t.classification.rocAuc.toFixed(3)} (0.5 = coin flip): ${t.classification.rocAuc > 0.55 ? 'some ability to separate outperformers from underperformers on this sample.' : 'little or no ability to tell outperformers from underperformers.'}`,
    })

  const train = r.trainMetrics.ranking.meanInformationCoefficient
  if (train !== null && rank.meanInformationCoefficient !== null && train - rank.meanInformationCoefficient > 0.1)
    out.push({ kind: 'caution', text: `The training-period IC (${train.toFixed(3)}) is much higher than the test IC – the model fitted patterns in the training data that did not carry over (overfitting).` })

  const p = e.latestPredictions
  if (p) {
    const selected = p.predictions.filter((x) => x.selected).sort((a, b) => b.targetWeight - a.targetWeight)
    out.push({
      kind: 'fact',
      text: `Turning predictions into a portfolio: the ${selected.length} highest-ranked stocks are held (${e.configuration.weighting === 'EqualWeight' ? 'equal weights' : e.configuration.weighting}), capped at ${share(e.configuration.maxWeight)} each, ${share(p.cashWeight)} in cash. ` +
        `Selected: ${selected.slice(0, 6).map((x) => `${x.symbol} ${share(x.targetWeight)}`).join(', ')}${selected.length > 6 ? ', …' : ''}.`,
    })
    if (selected.length >= e.symbols.length)
      out.push({ kind: 'caution', text: 'Every stock in the universe was selected, so the ML portfolio is just an equal-weight portfolio and its backtest cannot differ from equal weight. Use a larger universe or a smaller "Stocks held".' })
  }

  if (backtest) {
    const ml = backtest.performance.find((x) => x.name.toLowerCase().startsWith('ml'))
    const eq = backtest.performance.find((x) => x.name.toLowerCase().startsWith('equal'))
    if (ml && eq) out.push(...comparePerformance(ml, eq, 'the ML strategy', 'equal weight'))
  }
  out.push({ kind: 'caution', text: 'Prediction accuracy is not investment performance: a model can rank stocks slightly better than chance and still lose after costs and risk. Backtests are hypothetical, use today\'s index members (survivorship bias) and cannot guarantee future results.' })
  return out
}

// ------------------------------------------------------------------------------------------------ comparison

export function interpretComparison(c: StrategyComparison): Insight[] {
  const out: Insight[] = []
  const r = c.result
  if (!r) return out
  out.push({
    kind: 'fact',
    text: `All strategies were simulated on the same ${r.dates.length} trading days, the same ${r.universe.length} stocks, the same costs and rebalancing dates – differences come from the strategies, not the setup.`,
  })
  const by = (kind: string) => r.strategies.find((s) => s.kind === kind)
  // The two compared optimizations (any kinds: mathematical and/or ML), in the order A, B.
  const compared = r.strategies.filter((s) => s.kind === 'Mathematical' || s.kind === 'MachineLearning')
  const equal = by('EqualWeight')
  const bench = by('Benchmark')
  if (r.rankingBySharpe?.length)
    out.push({ kind: 'fact', text: `Ranked by Sharpe ratio over this period: ${r.rankingBySharpe.join(' › ')}.` })
  if (compared.length === 2) out.push(...comparePerformance(compared[0].performance, compared[1].performance, compared[0].name, compared[1].name))
  if (equal) for (const s of compared) out.push(...comparePerformance(s.performance, equal.performance, s.name, 'equal weight'))
  if (bench) {
    const beat = r.strategies.filter((s) => s.kind !== 'Benchmark' && s.performance.totalReturn > bench.performance.totalReturn).map((s) => s.name)
    out.push({ kind: 'fact', text: beat.length ? `Beat the benchmark's total return (${pct(bench.performance.totalReturn, 1)}): ${beat.join(', ')}.` : `No strategy beat the benchmark's total return of ${pct(bench.performance.totalReturn, 1)}.` })
  }
  const costly = r.strategies.filter((s) => s.performance.transactionCostsFraction > 0.005).sort((a, b) => b.performance.transactionCostsFraction - a.performance.transactionCostsFraction)[0]
  if (costly)
    out.push({ kind: 'interpretation', text: `${costly.name} traded the most: costs of ${pct(costly.performance.transactionCostsFraction, 2)} of capital (turnover ${pct(costly.performance.totalTurnover, 0)}). High turnover eats into returns – an advantage must survive these costs.` })
  out.push({ kind: 'caution', text: 'One evaluation period is a single draw of history: differences can be luck. A strategy that won here can lose in the next period.' })
  return out
}

function comparePerformance(a: StrategyPerformance, b: StrategyPerformance, aName: string, bName: string): Insight[] {
  const sa = a.sharpeRatio.value
  const sb = b.sharpeRatio.value
  const out: Insight[] = [{
    kind: 'fact',
    text: `Over the backtest ${aName} returned ${pct(a.annualizedReturn.value, 1)} a year with ${pct(a.annualizedVolatility.value, 1)} volatility and a worst drawdown of ${pct(a.maxDrawdown.value, 1)}; ` +
      `${bName} returned ${pct(b.annualizedReturn.value, 1)} with ${pct(b.annualizedVolatility.value, 1)} volatility.`,
  }]
  if (sa !== null && sb !== null)
    out.push({
      kind: 'interpretation',
      text: sa > sb
        ? `${capitalize(aName)} had the better risk-adjusted return (Sharpe ${sa.toFixed(2)} vs ${sb.toFixed(2)}) after transaction costs of ${pct(a.transactionCostsFraction, 2)} of capital.`
        : `${capitalize(aName)} did not beat ${bName} on a risk-adjusted basis (Sharpe ${sa.toFixed(2)} vs ${sb.toFixed(2)}), even though it cost ${pct(a.transactionCostsFraction, 2)} of capital in trading.`,
    })
  return out
}

const capitalize = (s: string) => s.charAt(0).toUpperCase() + s.slice(1)
