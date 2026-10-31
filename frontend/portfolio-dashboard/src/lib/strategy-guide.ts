import { pct } from './format'
import {
  equalWeight, maximumReturnForRisk, maximumSharpe, minimumVariance, sigmoid, targetReturn, twoAssets, type TwoAssetPortfolio,
} from './worked-examples'

/**
 * Strategy guide: one consistent 12-part explanation per implemented strategy. The content describes what the code
 * actually does (see docs/quantitative-strategies.md and docs/machine-learning.md); keep both in sync.
 */
export interface Formula {
  formula: string
  caption: string
}

export interface WorkedExample {
  intro: string
  table?: { columns: string[]; rows: string[][] }
  steps: string[]
  takeaway: string
}

export interface StrategyGuide {
  id: string
  family: 'optimization' | 'machine-learning' | 'evaluation'
  name: string
  tagline: string
  /** Where students run it in the app. */
  appLink: { to: string; label: string }
  what: string
  theory: string[]
  objective: string
  math: Formula[]
  variables: [string, string][]
  how: string[]
  inputs: string[]
  interpretation: string[]
  whenToUse: string[]
  advantages: string[]
  limitations: string[]
  example: WorkedExample
  results: string[]
  caveats: string[]
  related: string[]
}

const p1 = (x: number) => pct(x, 1)
const exampleRow = (label: string, x: TwoAssetPortfolio) => [label, p1(x.weightA), p1(x.weightB), p1(x.expectedReturn), p1(x.volatility), x.sharpe.toFixed(2)]
const exampleColumns = ['Portfolio', 'Weight A', 'Weight B', 'Expected return', 'Volatility', 'Sharpe']
const assumptions = `Two illustrative stocks (sample values, not market data): ${twoAssets.a.name} with ${p1(twoAssets.a.expectedReturn)} expected return and ${p1(twoAssets.a.volatility)} volatility, ` +
  `${twoAssets.b.name} with ${p1(twoAssets.b.expectedReturn)} and ${p1(twoAssets.b.volatility)}, correlation ${twoAssets.correlation}, risk-free rate ${p1(twoAssets.riskFree)}.`

const meanVarianceVariables: [string, string][] = [
  ['w', 'vector of portfolio weights (fractions of total portfolio value; long-only, 0 ≤ wᵢ)'],
  ['μ', 'vector of expected annual returns, estimated as the mean daily return × 252 over the lookback window'],
  ['Σ', 'annualized covariance matrix of daily returns (sample or Ledoit–Wolf shrinkage)'],
  ['wᵀΣw', 'portfolio variance; its square root is the portfolio volatility σ'],
  ['r_f', 'risk-free rate (annual, from the portfolio settings)'],
  ['c', 'cash reserve: the share of capital kept in cash, so Σwᵢ = 1 − c'],
  ['lᵢ, uᵢ', 'minimum and maximum weight per stock'],
]

const commonConstraints: Formula[] = [
  { formula: 'Σᵢ wᵢ = 1 − c', caption: 'fully invested apart from the cash reserve' },
  { formula: 'lᵢ ≤ wᵢ ≤ uᵢ,  wᵢ ≥ 0', caption: 'weight bounds; long-only – no short selling, no leverage' },
  { formula: 'Σ_{i∈sector s} wᵢ ≤ cap_s', caption: 'optional sector caps' },
  { formula: '½ Σᵢ |wᵢ − w₀ᵢ| ≤ T', caption: 'optional turnover limit relative to the current holdings w₀' },
]

const optimizationInputs = [
  'Daily adjusted closing prices of the selected stocks for the lookback window (default 756 trading days ≈ 3 years); stocks with fewer than 61 prices are excluded and reported.',
  'Returns are aligned on the dates all stocks traded (no forward-filling).',
  'Expected returns μ = historical mean daily return × 252 (arithmetic).',
  'Covariance Σ: sample estimator or Ledoit–Wolf shrinkage (default), annualized × 252.',
  'Risk-free rate, weight bounds, cash reserve and the optional constraints from the run settings.',
  'Stocks from several markets are combined in one analysis currency (the portfolio currency): prices are converted with the exchange rate of each date, so returns and risk include currency movements (unhedged). A single-market selection keeps its own market benchmark.',
]

const optimizationSolving = 'The problem is a convex quadratic program solved by the built-in ADMM solver (an implementation of the OSQP algorithm) with a polishing step for ~1e-9 accuracy. Every result is checked against every constraint; if the problem is infeasible or the solver fails, the run reports why instead of returning weights. Max-holdings and minimum-position limits use a heuristic (drop the smallest position and re-solve), which is feasible but not guaranteed optimal.'

const optimizationCaveats = [
  'All figures are estimates from history. Expected returns in particular are extremely noisy: a few years of data cannot pin down a stock\'s true expected return.',
  'Optimizers amplify estimation error – they favour stocks whose estimates happen to look good ("error maximization").',
  'In-sample statistics (on the data used to choose the weights) overstate what you can expect afterwards. Use the backtest or strategy comparison for an out-of-sample view.',
  'Paper trading only; nothing here is investment advice.',
]

export const strategyGuides: StrategyGuide[] = [
  // --------------------------------------------------------------------------------------------- optimization
  {
    id: 'minimum-variance',
    family: 'optimization',
    name: 'Minimum-variance portfolio',
    tagline: 'The least volatile mix of your stocks – ignores expected returns.',
    appLink: { to: '/optimize', label: 'Run it in Math Optimization (objective “Minimum variance”)' },
    what: 'Combines your stocks so that the portfolio fluctuates as little as possible, according to their past volatility and correlations. It does not try to earn the most – it tries to be calm.',
    theory: [
      'Diversification: when stocks do not move perfectly together (correlation below 1), their ups and downs partly cancel. A mix can be less volatile than any single stock in it.',
      'Modern portfolio theory (Markowitz, 1952) describes portfolios by expected return and variance. The minimum-variance portfolio is the left-most point of the efficient frontier.',
      'Because it only needs the covariance matrix – which can be estimated far more reliably than expected returns – it is often more stable out of sample than return-seeking optimizations.',
    ],
    objective: 'Minimize the estimated portfolio variance (equivalently, volatility) subject to the constraints.',
    math: [{ formula: 'minimize  wᵀ Σ w', caption: 'portfolio variance' }, ...commonConstraints],
    variables: meanVarianceVariables,
    how: [
      'Download and align daily adjusted closes; compute daily returns.',
      'Estimate the covariance matrix Σ (Ledoit–Wolf shrinkage by default, to stabilize it when there are many stocks and few observations).',
      'Solve the quadratic program “minimize wᵀΣw” under the budget, bound, sector and turnover constraints.',
      'Apply max-holdings / minimum-position heuristics if set, validate the weights against every constraint, and report diagnostics.',
      optimizationSolving,
    ],
    inputs: optimizationInputs,
    interpretation: [
      'A large weight means: this stock is calm and/or diversifies the others (low or negative correlation). It does not mean the stock is expected to perform well.',
      'Stocks with high expected returns can receive zero weight if they are volatile.',
      'Risk contribution shows how much of the portfolio variance each position causes; in a minimum-variance portfolio without binding bounds, marginal risk contributions are equal.',
    ],
    whenToUse: ['You care primarily about lower fluctuations.', 'You distrust return estimates (often wise).', 'As a defensive building block or teaching example of diversification.'],
    advantages: ['Needs no expected returns – the least error-prone input set.', 'Usually more stable weights than maximum Sharpe.', 'Clear economic intuition: diversification.'],
    limitations: [
      'Tends to concentrate in low-volatility sectors (utilities, staples) – check the sector concentration.',
      'Lowest risk is not the best risk-return trade-off; returns can be lower than other portfolios.',
      'Volatility and correlation also change over time; past calm does not guarantee future calm.',
    ],
    example: {
      intro: assumptions,
      table: { columns: exampleColumns, rows: [exampleRow('100% Stock A', { ...equalWeight(), ...portfolioAt(1) }), exampleRow('Minimum variance', minimumVariance()), exampleRow('Equal weight', equalWeight())] },
      steps: [
        'Formula for two stocks: w_A = (σ_B² − ρσ_Aσ_B) / (σ_A² + σ_B² − 2ρσ_Aσ_B).',
        `With the values above: w_A = ${p1(minimumVariance().weightA)}, w_B = ${p1(minimumVariance().weightB)}.`,
        `Portfolio volatility ${p1(minimumVariance().volatility)} – lower than Stock A alone (${p1(twoAssets.a.volatility)}), because the two stocks are only weakly correlated.`,
      ],
      takeaway: 'Adding a riskier but weakly correlated stock in a small dose reduced total risk. That is diversification at work.',
    },
    results: [
      'Optimize → result: “Allocation” shows the weights; “Risk contribution” shows who drives the risk; the frontier chart marks where the portfolio sits.',
      'The “What your result means” panel explains your largest positions, sector concentration and how your volatility compares with the benchmark.',
    ],
    caveats: optimizationCaveats,
    related: ['maximum-sharpe', 'equal-weight', 'target-return'],
  },
  {
    id: 'maximum-sharpe',
    family: 'optimization',
    name: 'Maximum-Sharpe (tangency) portfolio',
    tagline: 'The best estimated return per unit of risk – and the most sensitive to estimation error.',
    appLink: { to: '/optimize', label: 'Run it in Math Optimization (objective “Maximum Sharpe ratio”)' },
    what: 'Chooses weights that maximize the Sharpe ratio: estimated excess return over the risk-free rate divided by estimated volatility.',
    theory: [
      'The Sharpe ratio (Sharpe, 1966) measures reward per unit of total risk.',
      'In mean-variance theory the portfolio with the highest Sharpe ratio is the “tangency portfolio”: the point where a line from the risk-free rate touches the efficient frontier.',
      'Combining it with cash gives the best risk-return trade-off for any risk level – if the estimates were correct. In practice they are not, which is the central lesson.',
    ],
    objective: 'Maximize (μᵀw − r_f) / √(wᵀΣw) subject to the constraints.',
    math: [
      { formula: 'maximize  (μᵀw − r_f) / √(wᵀΣw)', caption: 'Sharpe ratio (not convex as written)' },
      { formula: 'minimize  yᵀΣy  s.t.  (μ − r_f)ᵀy = 1,  constraints scaled by κ ≥ 0,  w = y / κ', caption: 'equivalent convex form used by the solver (Cornuéjols–Tütüncü homogenization)' },
      ...commonConstraints,
    ],
    variables: [...meanVarianceVariables, ['y, κ', 'auxiliary variables of the convex reformulation; the weights are recovered as w = y / κ']],
    how: [
      'Estimate μ (mean daily return × 252) and Σ from the lookback window.',
      'If no stock beats the risk-free rate, the maximum Sharpe portfolio is undefined – the run explains this instead of returning weights.',
      'Solve the homogenized convex problem, recover the weights and validate them against every constraint.',
      optimizationSolving,
    ],
    inputs: [...optimizationInputs, 'Risk-free rate r_f (it changes which stocks look attractive).'],
    interpretation: [
      'Large weights go to stocks whose past return was high relative to their risk and that diversify the rest.',
      'A stock at the maximum weight means the optimizer “wanted” more of it – a sign the result leans heavily on that stock\'s return estimate.',
      'The estimated Sharpe ratio is in-sample: it describes the past window and is biased upwards as a forecast.',
    ],
    whenToUse: ['To learn how return estimates drive optimization.', 'With strict weight caps and a long, representative history.', 'As one candidate in a strategy comparison – never on its own.'],
    advantages: ['Theoretically optimal trade-off between risk and return if estimates were correct.', 'Explicitly uses the risk-free rate.'],
    limitations: [
      'Highly sensitive to expected-return estimates: small changes can flip the allocation.',
      'Often concentrated in recent winners (momentum in disguise).',
      'Out-of-sample Sharpe ratios are typically far below the in-sample estimate.',
    ],
    example: {
      intro: assumptions,
      table: { columns: exampleColumns, rows: [exampleRow('Maximum Sharpe', maximumSharpe()), exampleRow('Minimum variance', minimumVariance()), exampleRow('Equal weight', equalWeight())] },
      steps: [
        'For two stocks the tangency weights are proportional to Σ⁻¹(μ − r_f).',
        `Excess returns: A ${p1(twoAssets.a.expectedReturn - twoAssets.riskFree)}, B ${p1(twoAssets.b.expectedReturn - twoAssets.riskFree)}.`,
        `Result: w_A = ${p1(maximumSharpe().weightA)}, w_B = ${p1(maximumSharpe().weightB)}, Sharpe ${maximumSharpe().sharpe.toFixed(2)} – the highest of the three portfolios, as it must be by construction.`,
        `Change Stock B's expected return from 12% to 10% and B's weight drops sharply – try it with your own stocks by changing the lookback window.`,
      ],
      takeaway: 'Maximum Sharpe moves capital toward the stock with the better estimated reward per risk; the result depends strongly on those estimates.',
    },
    results: ['Compare your portfolio\'s in-sample Sharpe with the equal-weight reference in the result, then check a backtest – the gap usually shrinks.'],
    caveats: optimizationCaveats,
    related: ['minimum-variance', 'target-return', 'strategy-comparison'],
  },
  {
    id: 'target-return',
    family: 'optimization',
    name: 'Target-return portfolio',
    tagline: 'The lowest-risk way to reach a return you choose.',
    appLink: { to: '/optimize', label: 'Run it in Math Optimization (objective “Target return”)' },
    what: 'You set an expected annual return; the optimizer finds the least volatile combination of your stocks whose estimated return reaches it.',
    theory: [
      'Every target return between the minimum-variance return and the highest achievable return defines one point on the efficient frontier.',
      'Moving the target up trades more risk for more estimated return.',
    ],
    objective: 'Minimize wᵀΣw subject to μᵀw ≥ target and the other constraints.',
    math: [{ formula: 'minimize  wᵀΣw   subject to  μᵀw ≥ R_target', caption: 'one point of the efficient frontier' }, ...commonConstraints],
    variables: [...meanVarianceVariables, ['R_target', 'required expected annual return']],
    how: [
      'Estimate μ and Σ.',
      'Check first that the target is achievable (at most the highest return reachable under the constraints); otherwise the run is reported as infeasible with the maximum achievable return.',
      'Solve the quadratic program and validate the result.',
      optimizationSolving,
    ],
    inputs: [...optimizationInputs, 'The target return (annual, as a fraction).'],
    interpretation: [
      'If the target is below the minimum-variance return, the constraint does not bind and you get the minimum-variance portfolio.',
      'If it binds, the portfolio shifts toward higher-return (usually more volatile) stocks just enough to reach the target.',
    ],
    whenToUse: ['To explore the efficient frontier step by step.', 'When a return requirement is given and you want the least risk for it (in the estimated world).'],
    advantages: ['Intuitive control over the risk/return trade-off.', 'Convex and exactly solvable.'],
    limitations: ['Relies on expected-return estimates like maximum Sharpe.', 'A high target forces concentration in a few high-return stocks.'],
    example: {
      intro: assumptions,
      table: { columns: exampleColumns, rows: [exampleRow('Target 9%', targetReturn(0.09)), exampleRow('Target 10%', targetReturn(0.1)), exampleRow('Target 11%', targetReturn(0.11))] },
      steps: [
        `The minimum-variance portfolio already earns ${p1(minimumVariance().expectedReturn)}, so a 9% target does not bind and you get that portfolio.`,
        'For a binding target with two stocks, the return condition alone fixes the weights: w_A = (μ_B − target) / (μ_B − μ_A).',
        `Each extra percentage point of target return costs extra volatility: ${p1(targetReturn(0.1).volatility)} at 10%, ${p1(targetReturn(0.11).volatility)} at 11%.`,
      ],
      takeaway: 'Higher required return → more weight in the volatile stock → more risk. That is the efficient frontier in action.',
    },
    results: ['The frontier chart shows your portfolio and the frontier; the interpretation panel tells you whether the target constraint was binding.'],
    caveats: optimizationCaveats,
    related: ['minimum-variance', 'maximum-return-for-risk', 'maximum-sharpe'],
  },
  {
    id: 'maximum-return-for-risk',
    family: 'optimization',
    name: 'Maximum return for a volatility limit',
    tagline: 'The highest estimated return without exceeding the risk you accept.',
    appLink: { to: '/optimize', label: 'Run it in Math Optimization (objective “Max return for risk limit”)' },
    what: 'You set a maximum volatility; the optimizer finds the portfolio with the highest estimated return whose estimated volatility stays within that limit.',
    theory: ['This is the mirror image of the target-return problem: it fixes risk instead of return. Its solution also lies on the efficient frontier.'],
    objective: 'Maximize μᵀw subject to √(wᵀΣw) ≤ σ_max and the other constraints.',
    math: [
      { formula: 'maximize  μᵀw   subject to  √(wᵀΣw) ≤ σ_max', caption: 'quadratic (not linear) risk constraint' },
      { formula: 'find R* with σ(R*) = σ_max by bisection on target-return problems', caption: 'how the app solves it: a sequence of exact convex QPs' },
      ...commonConstraints,
    ],
    variables: [...meanVarianceVariables, ['σ_max', 'volatility limit (annual)'], ['σ(R)', 'volatility of the minimum-variance portfolio with expected return R']],
    how: [
      'Check that the limit is at least the minimum-variance volatility; otherwise no portfolio qualifies and the run explains why.',
      'Repeatedly solve target-return problems, bisecting on the target until the portfolio volatility equals the limit (tolerance 1e-9 in return).',
      optimizationSolving,
    ],
    inputs: [...optimizationInputs, 'The volatility limit (annual, as a fraction).'],
    interpretation: ['If the limit is above the volatility of the highest-return portfolio, the constraint does not bind and you get the maximum-return portfolio under the other constraints.'],
    whenToUse: ['When a risk budget is given (e.g. “no more than 15% volatility”).', 'To compare how much estimated return each extra unit of risk buys.'],
    advantages: ['Speaks the language of risk budgets.', 'Exact up to a tiny numerical tolerance.'],
    limitations: ['Volatility is estimated from the past and can be exceeded in reality.', 'Still depends on expected-return estimates.'],
    example: {
      intro: assumptions,
      table: {
        columns: exampleColumns,
        rows: [0.185, 0.2, 0.25].map((l) => maximumReturnForRisk(l)).filter((x): x is TwoAssetPortfolio => x !== null)
          .map((x, i) => exampleRow(`Limit ${['18.5%', '20%', '25%'][i]}`, x)),
      },
      steps: [
        `The least volatile mix has ${p1(minimumVariance().volatility)} volatility; any lower limit is infeasible.`,
        'Raising the limit lets the optimizer add more of the higher-return Stock B until the limit binds.',
      ],
      takeaway: 'Each risk limit maps to one point on the frontier; loosening the limit buys estimated return at a growing risk cost.',
    },
    results: ['The result reports the achieved volatility next to your limit; the frontier chart shows where you landed.'],
    caveats: optimizationCaveats,
    related: ['target-return', 'minimum-variance'],
  },
  {
    id: 'equal-weight',
    family: 'optimization',
    name: 'Equal-weight portfolio (1/N)',
    tagline: 'Same share for every stock – the honest baseline.',
    appLink: { to: '/optimize', label: 'Run it in Math Optimization (objective “Equal weight”)' },
    what: 'Splits the invested capital equally across all eligible stocks. No estimates, no optimization.',
    theory: [
      'Research (DeMiguel, Garlappi & Uppal, 2009) found that many optimized portfolios fail to beat 1/N out of sample, because estimation error outweighs the theoretical gains.',
      'It is therefore the benchmark every strategy in this app is compared with.',
    ],
    objective: 'None – the weights follow a rule: wᵢ = (1 − c) / N.',
    math: [{ formula: 'wᵢ = (1 − c) / N   for each of the N eligible stocks', caption: 'equal split of the invested capital' }],
    variables: [['N', 'number of stocks with sufficient price history'], ['c', 'cash reserve']],
    how: ['Exclude stocks without enough price history.', 'Give each remaining stock the same weight.', 'In backtests and comparisons it is rebalanced back to equal weights on every rebalancing date.'],
    inputs: ['The selected stocks and the cash reserve. Prices are only used to compute the statistics shown next to the weights.'],
    interpretation: ['Every stock matters equally regardless of size or risk – volatile stocks therefore dominate the portfolio\'s risk.'],
    whenToUse: ['Always, as the reference point.', 'When you have no reliable view on returns or risks.'],
    advantages: ['Zero estimation error.', 'Transparent and hard to beat out of sample.', 'Automatic diversification across all selected stocks.'],
    limitations: ['Ignores differences in risk and correlation.', 'Rebalancing back to equal weights creates turnover and costs.'],
    example: {
      intro: assumptions,
      table: { columns: exampleColumns, rows: [exampleRow('Equal weight', equalWeight()), exampleRow('Minimum variance', minimumVariance()), exampleRow('Maximum Sharpe', maximumSharpe())] },
      steps: ['Each stock receives 50%.', `Its volatility ${p1(equalWeight().volatility)} is higher than the minimum-variance mix, its Sharpe ${equalWeight().sharpe.toFixed(2)} below the tangency portfolio – in theory.`],
      takeaway: 'On paper equal weight is beaten; in practice, with estimated inputs, it often is not. Test it yourself with Strategy comparison.',
    },
    results: ['Every optimization result lists equal weight as a reference; Strategy comparison simulates it on identical dates and costs.'],
    caveats: optimizationCaveats,
    related: ['strategy-comparison', 'minimum-variance', 'maximum-sharpe'],
  },

  // ----------------------------------------------------------------------------------------- machine learning
  ...mlGuides(),

  // ------------------------------------------------------------------------------------------------ evaluation
  {
    id: 'ml-portfolio-construction',
    family: 'evaluation',
    name: 'From predictions to an ML portfolio',
    tagline: 'How model scores become weights – separately from the optimizer.',
    appLink: { to: '/ml-optimize', label: 'Configure it in ML Optimization (Portfolio construction)' },
    what: 'A model only produces a score per stock. A separate, transparent rule turns those scores into portfolio weights; the mean-variance optimizer is not used.',
    theory: ['Separating prediction from portfolio construction makes it clear whether a result comes from the model\'s skill or from the weighting rule.'],
    objective: 'Hold the stocks the model ranks highest, within diversification and turnover limits.',
    math: [
      { formula: 'S = top K stocks by score (score ≥ minimum, if set)', caption: 'selection' },
      { formula: 'wᵢ = 1/K  |  ∝ (K − rankᵢ + 1)  |  ∝ 1/σᵢ  |  ∝ scoreᵢ − min_S score + 0.1·spread_S', caption: 'equal, rank-linear, inverse-volatility or score-proportional weights (S = the selected stocks; the 10% spread keeps the weakest selected stock in)' },
      { formula: 'wᵢ ≤ cap,  Σ_sector wᵢ ≤ sector cap,  Σwᵢ ≤ 1 − c', caption: 'iterative capping; what cannot be placed stays in cash' },
      { formula: 'w = w₀ + λ(w* − w₀),  λ chosen so that ½Σ|w − w₀| ≤ T', caption: 'optional turnover limit by partial rebalancing' },
    ],
    variables: [['K', 'number of stocks held (top K)'], ['σᵢ', 'trailing 63-day volatility'], ['w₀', 'weights before rebalancing'], ['T', 'maximum one-way turnover']],
    how: ['Score every stock with the trained model on the latest date.', 'Rank (lower predicted volatility ranks higher for the volatility target).', 'Select, weight, cap, limit turnover; validate weights (≥ 0, sum ≤ 1).'],
    inputs: ['Model scores, the portfolio-construction settings of the experiment, trailing volatilities for inverse-volatility weighting.'],
    interpretation: ['A stock\'s weight reflects its rank and the rule, not a predicted price target. Equal-weight construction ignores how much better rank 1 is than rank 5.'],
    whenToUse: ['Always when turning ML scores into a portfolio in this app.'],
    advantages: ['Transparent; easy to attribute results to the model vs. the rule.', 'Caps and turnover limits control concentration and trading costs.'],
    limitations: ['With K ≥ the number of stocks every stock is held – the result is just equal weight.', 'No risk model: correlations between selected stocks are ignored (unlike mean-variance).'],
    example: {
      intro: 'Illustrative scores for five stocks (sample values): A 0.71, B 0.64, C 0.58, D 0.55, E 0.49; top K = 3, cap 40%, no cash reserve.',
      table: { columns: ['Rule', 'A', 'B', 'C'], rows: [['Equal weight', '33.3%', '33.3%', '33.3%'], ['Rank-linear before cap (3:2:1)', '50.0%', '33.3%', '16.7%'], ['Rank-linear after 40% cap', '40.0%', '40.0%', '20.0%']] },
      steps: ['Top 3 by score: A, B, C.', 'Equal weight gives each 1/3.', 'Rank-linear gives 3/6, 2/6, 1/6. A exceeds the 40% cap; its excess 10% is shared by B and C in proportion to their weights (2:1), so B reaches 40% and C 20%.'],
      takeaway: 'The same predictions can produce quite different portfolios depending on the construction rule.',
    },
    results: ['ML workspace → “Predictions & portfolio” lists every stock with score, rank, selection and weight, and explains each construction step.'],
    caveats: ['The rule does not make a weak model strong: with no predictive skill, the portfolio is effectively a random subset.'],
    related: ['ridge-regression', 'gradient-boosting', 'strategy-comparison'],
  },
  {
    id: 'strategy-comparison',
    family: 'evaluation',
    name: 'Strategy comparison and backtesting',
    tagline: 'Optimization vs. ML vs. equal weight vs. benchmark – on identical terms.',
    appLink: { to: '/strategy-comparison', label: 'Open Strategy comparison' },
    what: 'Re-runs the mathematical strategy, the ML strategy, equal weight and the benchmark through one shared simulator, with the same dates, stocks, capital, costs and rebalancing, so the results are comparable.',
    theory: [
      'A walk-forward backtest makes every decision with data available at that time only, then measures what happened next.',
      'Fair comparison requires identical conventions; otherwise differences reflect the setup, not the strategy.',
    ],
    objective: 'Measure realized (hypothetical) performance and risk out of sample.',
    math: [
      { formula: 'decision at close of day t uses prices ≤ t;  orders fill at the close of day t+1', caption: 'no look-ahead' },
      { formula: 'cost = bps × |trade value|;  cash earns 0%', caption: 'same costs and cash treatment for every strategy' },
      { formula: 'Sharpe = (mean daily return − r_f/252) / sd × √252', caption: 'same metric code for every strategy' },
    ],
    variables: [['t', 'rebalancing (signal) date'], ['bps', 'transaction cost in basis points (0.01%)']],
    how: [
      'Fix the evaluation period, universe (intersection or union of both runs), capital and costs.',
      'Classical strategy: re-optimize with the run\'s stored settings on a trailing window at each rebalance.',
      'ML strategy: retrain with the experiment\'s stored hyperparameters on labels known before each rebalance.',
      'Simulate all strategies in one engine and compute identical metrics.',
    ],
    inputs: ['A successful optimization run, a successful ML experiment, the evaluation window, costs and rebalancing frequency.'],
    interpretation: ['Look at risk-adjusted return (Sharpe), drawdowns and turnover together; a higher return with much higher risk or costs is not necessarily better.'],
    whenToUse: ['Before trusting any optimization or model result.'],
    advantages: ['Same simulator and conventions for every strategy.', 'Stored comparisons are immutable and reproducible.'],
    limitations: ['One historical period – results can be luck.', 'Today\'s index members only (survivorship bias); no market impact.'],
    example: {
      intro: 'A typical outcome in this app (see docs/implementation-status.md): on 10 large US stocks over Dec 2025 – Sep 2026, equal weight had a Sharpe ratio of 1.61; the best ML strategy 0.92.',
      steps: ['All strategies used the same dates, costs and stocks.', 'The ML strategies were more volatile because they concentrated in five stocks.'],
      takeaway: 'Simple baselines are hard to beat; a comparison shows this honestly.',
    },
    results: ['The comparison page shows growth, drawdowns, rolling Sharpe, allocations and a written interpretation of the outcome.'],
    caveats: ['Hypothetical results; not a forecast. Differences over one period may be due to chance.'],
    related: ['equal-weight', 'ml-portfolio-construction', 'maximum-sharpe'],
  },
]

function portfolioAt(weightA: number) {
  const twoA = twoAssets.a
  return { weightA, weightB: 1 - weightA, expectedReturn: twoA.expectedReturn, volatility: twoA.volatility, sharpe: (twoA.expectedReturn - twoAssets.riskFree) / twoA.volatility }
}

function mlGuides(): StrategyGuide[] {
  const featureInputs = [
    '20 price- and volume-based features per stock and date (feature set features-2026.10-v1): momentum over 5 days to 12 months, distance to moving averages, volatility and downside volatility, drawdown, largest daily gain, beta and correlation to the benchmark, relative volume, dollar volume, sector-relative momentum and market trend/volatility.',
    'Features at date t use prices up to and including t only.',
    'A target (label) per stock and date, measured over the next h trading days (5, 21 or 63): forward return, forward excess return over the benchmark, outperformance yes/no, or forward volatility.',
    'At least 5 stocks in one currency, several years of daily prices, and the benchmark.',
  ]
  const validation = [
    'Decision dates are split chronologically: 60% training, 20% validation, 20% test – never randomly.',
    'Purging: training samples whose outcome window overlaps a later period are removed, so no future information leaks backward.',
    'Hyperparameters are chosen on validation only; the test period is used exactly once.',
    'Scaling and missing-value imputation are fitted on training rows only.',
  ]
  const mlCaveats = [
    'Stock returns are mostly noise; useful predictive signals are weak and unstable. Most experiments in this app show no statistically significant skill.',
    'Prediction accuracy is not investment performance – check the backtest and the comparison after costs.',
    'Today\'s index members are used (survivorship bias), and trying many configurations on the same data inflates the best result (multiple testing).',
  ]
  const mlResults = [
    'ML workspace → Evaluation: test-period metrics next to naive baselines; Explainability: which features the model relies on; Backtest: walk-forward performance vs. equal weight and the benchmark.',
    'The “What your result means” panel states whether the skill is statistically distinguishable from zero and how the portfolio was built.',
  ]
  const commonTheory = 'Supervised learning: the model learns a mapping from features (what we know at t) to a target (what happened after t) on past examples, then applies it to new dates.'

  return [
    {
      id: 'ridge-regression',
      family: 'machine-learning',
      name: 'Ridge regression',
      tagline: 'A linear model with shrinkage – the transparent baseline.',
      appLink: { to: '/ml-optimize', label: 'Run it in ML Optimization (model “Ridge regression”)' },
      what: 'Predicts a stock\'s future return (or excess return, or volatility) as a weighted sum of its features. A penalty keeps the weights small to avoid fitting noise.',
      theory: [commonTheory, 'Ordinary least squares overfits when features are many and correlated. Ridge (Hoerl & Kennard, 1970) adds an L2 penalty that shrinks coefficients toward zero, trading a little bias for much less variance.'],
      objective: 'Minimize squared prediction errors plus a penalty on the size of the coefficients.',
      math: [
        { formula: 'ŷ = b + Σⱼ βⱼ zⱼ', caption: 'prediction from standardized features z' },
        { formula: 'minimize  Σᵢ (yᵢ − ŷᵢ)² + λ·n·Σⱼ βⱼ²', caption: 'least squares + L2 penalty (λ scaled by the sample count n)' },
        { formula: 'β = (ZᵀZ + λnI)⁻¹ Zᵀ(y − ȳ)', caption: 'closed-form solution used in the app' },
      ],
      variables: [['zⱼ', 'feature j, standardized with the training mean and standard deviation'], ['βⱼ', 'coefficient of feature j'], ['b', 'intercept (training mean of y)'], ['λ', 'penalty strength, chosen on validation from {0.001, 0.01, 0.1, 1}'], ['y', 'target, e.g. 21-day forward excess return']],
      how: ['Build features and targets on the decision grid.', ...validation, 'Fit β for each λ on training data, pick the λ with the best validation result, refit on training + validation, evaluate once on test.', 'Refit on all data with known outcomes for current predictions.'],
      inputs: featureInputs,
      interpretation: ['Coefficients show how predictions change with each standardized feature, holding the others fixed – associations in the training data, not causes.', 'Per-stock drivers are exact: βⱼ·zⱼ adds up to the prediction.'],
      whenToUse: ['As the first, explainable model for any return or volatility target.', 'To check whether a more complex model adds anything.'],
      advantages: ['Fast, deterministic, fully explainable.', 'Robust to correlated features thanks to the penalty.'],
      limitations: ['Only linear relationships.', 'Predicts the size of returns poorly (out-of-sample R² is usually ≤ 0 for returns).'],
      example: {
        intro: 'Illustrative coefficients (sample values): intercept 0.10%, momentum (mom_12_1) β = 0.40%, volatility (vol_63) β = −0.25% per standard deviation.',
        table: { columns: ['Stock', 'z(momentum)', 'z(volatility)', 'Prediction'], rows: [['X', '+1.5', '+0.5', `${(0.1 + 0.4 * 1.5 - 0.25 * 0.5).toFixed(3)}%`], ['Y', '−0.5', '−1.0', `${(0.1 + 0.4 * -0.5 - 0.25 * -1).toFixed(3)}%`]] },
        steps: ['Prediction = intercept + Σ β × z.', 'X: 0.10 + 0.40×1.5 − 0.25×0.5 = 0.575% expected 21-day excess return.', 'Y: 0.10 − 0.20 + 0.25 = 0.150%. X ranks above Y.'],
        takeaway: 'The ranking, not the exact number, is what the portfolio uses.',
      },
      results: mlResults,
      caveats: mlCaveats,
      related: ['logistic-regression', 'gradient-boosting', 'ml-portfolio-construction'],
    },
    {
      id: 'logistic-regression',
      family: 'machine-learning',
      name: 'Logistic regression',
      tagline: 'The probability that a stock beats the benchmark.',
      appLink: { to: '/ml-optimize', label: 'Run it in ML Optimization (target “Probability of outperformance”)' },
      what: 'Estimates the probability that a stock\'s return over the next h days exceeds the benchmark\'s.',
      theory: [commonTheory, 'Logistic regression models the log-odds of a yes/no outcome as a linear function of the features; an L2 penalty keeps coefficients finite and stable.'],
      objective: 'Maximize the (penalized) likelihood of the observed outperform/underperform outcomes.',
      math: [
        { formula: 'P(outperform) = σ(b + Σⱼ βⱼ zⱼ),   σ(x) = 1 / (1 + e⁻ˣ)', caption: 'probability from a linear score' },
        { formula: 'minimize  −Σᵢ [yᵢ log pᵢ + (1 − yᵢ) log(1 − pᵢ)] + (λn/2) Σⱼ βⱼ²', caption: 'penalized log loss, solved by Newton’s method (IRLS)' },
      ],
      variables: [['yᵢ', '1 if the stock beat the benchmark over the horizon, else 0'], ['pᵢ', 'predicted probability'], ['λ', 'penalty, chosen on validation']],
      how: ['Label each stock-date with 1/0 for outperformance.', ...validation, 'Fit by iteratively reweighted least squares; rank stocks by probability.'],
      inputs: featureInputs,
      interpretation: ['A probability of 55% means: in similar past situations, about 55% of stocks outperformed – a small edge at best.', 'Calibration chart: are 60% predictions right 60% of the time?'],
      whenToUse: ['When the question is “will it beat the benchmark?” rather than “by how much?”.'],
      advantages: ['Interpretable probabilities and exact per-feature contributions (on the log-odds scale).', 'Less sensitive to outliers than regression on returns.'],
      limitations: ['Linear in the log-odds; ignores how large the outperformance is.', 'Probabilities near 50% are common – check AUC and calibration.'],
      example: {
        intro: 'Illustrative model (sample values): b = 0, β(momentum) = 0.3.',
        table: { columns: ['z(momentum)', 'Score', 'Probability'], rows: [-1, 0, 1, 2].map((z) => [String(z), (0.3 * z).toFixed(1), pct(sigmoid(0.3 * z), 1)]) },
        steps: ['Score = b + β × z.', 'Probability = 1 / (1 + e^(−score)).', 'Even a strong +2 SD momentum reading gives only about 65% – realistic edges are small.'],
        takeaway: 'The model ranks stocks by probability; the portfolio holds the highest-probability ones.',
      },
      results: mlResults,
      caveats: mlCaveats,
      related: ['ridge-regression', 'gradient-boosting', 'ml-portfolio-construction'],
    },
    {
      id: 'random-forest',
      family: 'machine-learning',
      name: 'Random forest',
      tagline: 'Many decision trees, averaged – captures non-linear patterns.',
      appLink: { to: '/ml-optimize', label: 'Run it in ML Optimization (model “Random forest”)' },
      what: 'Builds hundreds of decision trees on random subsets of the data and features and averages their predictions.',
      theory: [commonTheory, 'A single decision tree splits the data by feature thresholds (e.g. “momentum > 5%”) and is unstable. Averaging many decorrelated trees (bagging + random feature subsets; Breiman, 2001) reduces that variance.'],
      objective: 'Each tree minimizes squared error in its leaves; the forest averages the trees.',
      math: [
        { formula: 'ŷ(x) = (1/T) Σₜ treeₜ(x)', caption: 'average of T trees' },
        { formula: 'split chosen to minimize Σ_left (y − ȳ_left)² + Σ_right (y − ȳ_right)²', caption: 'regression-tree splitting rule' },
      ],
      variables: [['T', 'number of trees (200)'], ['leaves', 'maximum leaves per tree (16 or 64, chosen on validation)'], ['min leaf', 'minimum samples per leaf (20 or 100)'], ['feature fraction', 'share of features considered per tree (0.6)']],
      how: ['Impute missing feature values with training medians.', ...validation, 'Train with ML.NET FastForest; for the outperformance target the forest\'s average of 0/1 labels is the probability.'],
      inputs: featureInputs,
      interpretation: ['Feature importance (split gain) shows which features the trees use most – correlated features share importance.', 'Per-stock drivers are approximations (occlusion: change in prediction when a feature is set to its training median).'],
      whenToUse: ['When relationships may be non-linear or involve interactions (e.g. momentum only matters in calm markets).'],
      advantages: ['Captures non-linearities without manual feature engineering.', 'Robust to outliers; averaging limits overfitting.'],
      limitations: ['Less transparent than linear models.', 'Can still overfit noisy financial data; needs many samples.'],
      example: {
        intro: 'Illustrative (sample values): three tiny trees predict a stock\'s 21-day excess return.',
        table: { columns: ['Tree', 'Rule applied', 'Prediction'], rows: [['1', 'momentum > 5% → leaf', '+0.6%'], ['2', 'volatility < 25% → leaf', '+0.2%'], ['3', 'drawdown < −20% → leaf', '−0.2%']] },
        steps: ['Each tree routes the stock to one leaf.', 'Forest prediction = average = (0.6 + 0.2 − 0.2) / 3 = 0.2%.'],
        takeaway: 'Disagreeing trees cancel out; only patterns many trees agree on survive the average.',
      },
      results: mlResults,
      caveats: mlCaveats,
      related: ['gradient-boosting', 'ridge-regression'],
    },
    {
      id: 'gradient-boosting',
      family: 'machine-learning',
      name: 'Gradient boosting',
      tagline: 'Trees added one after another, each fixing the previous errors.',
      appLink: { to: '/ml-optimize', label: 'Run it in ML Optimization (model “Gradient boosting”)' },
      what: 'Builds small decision trees sequentially; each new tree is fitted to the errors of the model so far, and its contribution is scaled down by a learning rate.',
      theory: [commonTheory, 'Gradient boosting (Friedman, 2001) performs gradient descent in function space: every tree moves the predictions a small step in the direction that reduces the loss.'],
      objective: 'Minimize the loss (squared error, or log loss for the outperformance target) by adding trees greedily.',
      math: [
        { formula: 'F₀(x) = mean of y;   F_m(x) = F_{m−1}(x) + η · tree_m(x)', caption: 'additive model' },
        { formula: 'tree_m fitted to the residuals rᵢ = yᵢ − F_{m−1}(xᵢ)', caption: 'for squared error the gradient is the residual' },
      ],
      variables: [['η', 'learning rate (0.05)'], ['M', 'number of trees (100 or 300, chosen on validation)'], ['leaves', '8 or 16 per tree'], ['min leaf', '50 samples']],
      how: ['Impute missing values with training medians.', ...validation, 'Train with ML.NET FastTree (MART); for the outperformance target a binary FastTree classifier is used.'],
      inputs: featureInputs,
      interpretation: ['Same explainability as the random forest: split-gain importance, permutation importance on the test period, occlusion drivers per stock.'],
      whenToUse: ['When you want a flexible non-linear model and have enough data; compare against ridge and baselines.'],
      advantages: ['Often the most accurate tabular model in general machine learning.', 'Learning rate and small trees give fine control over complexity.'],
      limitations: ['Easiest model here to overfit noisy returns – watch the gap between training and test metrics.', 'Less transparent; more hyperparameters.'],
      example: {
        intro: 'Illustrative (sample values): actual outcome +1.0%, learning rate 0.5 for readability (the app uses 0.05).',
        table: { columns: ['Step', 'Model prediction', 'Residual'], rows: [['Start (mean)', '0.20%', '0.80%'], ['+ tree 1 (predicts 0.80%) × 0.5', '0.60%', '0.40%'], ['+ tree 2 (predicts 0.40%) × 0.5', '0.80%', '0.20%']] },
        steps: ['Each tree predicts the remaining error.', 'Adding a fraction of it moves the prediction closer to the outcome.', 'On noisy data, continuing too long fits the noise – that is why validation chooses the number of trees.'],
        takeaway: 'Small, careful steps reduce errors on training data; only out-of-sample tests show if it generalizes.',
      },
      results: mlResults,
      caveats: mlCaveats,
      related: ['random-forest', 'ridge-regression'],
    },
  ]
}

export const guideById = (id: string) => strategyGuides.find((g) => g.id === id)

/** Guide for an optimization objective or ML model family used in a run. */
export const guideForObjective = (objective: string) =>
  ({ MinimumVariance: 'minimum-variance', MaximumSharpe: 'maximum-sharpe', TargetReturn: 'target-return', MaximumReturnForRisk: 'maximum-return-for-risk', EqualWeight: 'equal-weight' } as Record<string, string>)[objective]

export const guideForModel = (family: string) =>
  ({ RidgeRegression: 'ridge-regression', LogisticRegression: 'logistic-regression', RandomForest: 'random-forest', GradientBoosting: 'gradient-boosting' } as Record<string, string>)[family]
