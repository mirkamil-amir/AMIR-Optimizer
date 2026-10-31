/**
 * Calculations behind the interactive figures of the strategy guide (Codex). Everything here works on illustrative sample
 * data defined in this file or generated from a fixed seed – never on stored prices, the API or a user's portfolios – so
 * the figures are reproducible and cannot be mistaken for market data. Pure functions, unit-tested in guide-demos.test.ts.
 */
import { twoAssets } from './worked-examples'

// ------------------------------------------------------------------ two stocks (mean-variance guides)

export interface TwoAssetParams { muA: number; muB: number; volA: number; volB: number; rho: number; rf: number }

/** The worked example's two stocks (Stock A steady, Stock B growth), as the starting point of every slider. */
export const sampleTwoAssets: TwoAssetParams = {
  muA: twoAssets.a.expectedReturn, muB: twoAssets.b.expectedReturn, volA: twoAssets.a.volatility, volB: twoAssets.b.volatility,
  rho: twoAssets.correlation, rf: twoAssets.riskFree,
}

export interface Mix { wA: number; ret: number; vol: number; sharpe: number }

const clamp01 = (x: number) => Math.min(1, Math.max(0, x))

export function mix(p: TwoAssetParams, wA: number): Mix {
  const wB = 1 - wA
  const cov = p.rho * p.volA * p.volB
  const vol = Math.sqrt(Math.max(0, wA ** 2 * p.volA ** 2 + wB ** 2 * p.volB ** 2 + 2 * wA * wB * cov))
  const ret = wA * p.muA + wB * p.muB
  return { wA, ret, vol, sharpe: vol > 0 ? (ret - p.rf) / vol : 0 }
}

/** Every long-only mix from 100% B (wA = 0) to 100% A (wA = 1). */
export function mixCurve(p: TwoAssetParams, points = 101): Mix[] {
  return Array.from({ length: points }, (_, i) => mix(p, i / (points - 1)))
}

/** Global minimum variance, long-only. */
export function gmvMix(p: TwoAssetParams): Mix {
  const cov = p.rho * p.volA * p.volB
  const denom = p.volA ** 2 + p.volB ** 2 - 2 * cov
  return mix(p, denom > 0 ? clamp01((p.volB ** 2 - cov) / denom) : 0.5)
}

/** Tangency (maximum Sharpe), long-only: w ∝ Σ⁻¹(μ − r_f); falls back to the better single stock when the ratio flips sign. */
export function tangencyMix(p: TwoAssetParams): Mix {
  const ea = p.muA - p.rf
  const eb = p.muB - p.rf
  const cov = p.rho * p.volA * p.volB
  const xa = p.volB ** 2 * ea - cov * eb
  const xb = -cov * ea + p.volA ** 2 * eb
  const candidates = [mix(p, 0), mix(p, 1)]
  if (xa + xb > 0) candidates.push(mix(p, clamp01(xa / (xa + xb))))
  return candidates.reduce((best, m) => (m.sharpe > best.sharpe ? m : best))
}

/** Lowest-risk mix whose expected return reaches the target; null when no long-only mix reaches it. */
export function targetReturnMix(p: TwoAssetParams, target: number): Mix | null {
  const gmv = gmvMix(p)
  if (gmv.ret >= target) return gmv
  if (Math.max(p.muA, p.muB) < target) return null
  if (p.muA === p.muB) return gmv
  return mix(p, clamp01((p.muB - target) / (p.muB - p.muA)))
}

/** Highest expected return with volatility ≤ limit (bisection along the efficient branch); null when even GMV is above it. */
export function maxReturnForRiskMix(p: TwoAssetParams, limit: number): Mix | null {
  const gmv = gmvMix(p)
  if (gmv.vol > limit + 1e-12) return null
  const hiRet = p.muB >= p.muA ? 0 : 1 // the end of the efficient branch: all in the higher-return stock
  if (mix(p, hiRet).vol <= limit) return mix(p, hiRet)
  let safe = gmv.wA
  let over = hiRet
  for (let i = 0; i < 60; i++) {
    const mid = (safe + over) / 2
    if (mix(p, mid).vol > limit) over = mid
    else safe = mid
  }
  return mix(p, safe)
}

// ------------------------------------------------------------------ equal weight: diversification with 1/N

/** Volatility of an equal-weight portfolio of N stocks that all have volatility σ and pairwise correlation ρ. */
export const equalWeightVolatility = (n: number, sigma: number, rho: number) => sigma * Math.sqrt(rho + (1 - rho) / n)

// ------------------------------------------------------------------ ML portfolio construction

export interface ScoredStock { symbol: string; score: number }

/** Eight sample stocks with illustrative model scores (the guide's five plus three more), not real companies. */
export const sampleScores: ScoredStock[] = [
  { symbol: 'A', score: 0.71 }, { symbol: 'B', score: 0.64 }, { symbol: 'C', score: 0.58 }, { symbol: 'D', score: 0.55 },
  { symbol: 'E', score: 0.49 }, { symbol: 'F', score: 0.46 }, { symbol: 'G', score: 0.41 }, { symbol: 'H', score: 0.37 },
]

export type WeightingRule = 'equal' | 'rank' | 'score'

/**
 * The guide's construction rule: top K by score, weights by rule, then iterative capping – the excess above the cap goes
 * to the uncapped stocks in proportion to their weights; what nobody can take stays in cash.
 */
export function constructWeights(stocks: ScoredStock[], topK: number, rule: WeightingRule, cap: number): { weights: Record<string, number>; cash: number } {
  const ranked = [...stocks].sort((a, b) => b.score - a.score)
  const k = Math.max(1, Math.min(topK, ranked.length))
  const chosen = ranked.slice(0, k)
  const minScore = Math.min(...ranked.map((s) => s.score))
  const raw = chosen.map((s, i) => (rule === 'equal' ? 1 : rule === 'rank' ? k - i : Math.max(s.score - minScore, 1e-9)))
  const total = raw.reduce((a, b) => a + b, 0)
  const w = raw.map((x) => x / total)
  const capped = new Set<number>()
  for (let round = 0; round < k; round++) {
    let excess = 0
    w.forEach((x, i) => { if (x > cap + 1e-12) { excess += x - cap; w[i] = cap; capped.add(i) } })
    if (excess <= 1e-12) break
    const free = w.map((x, i) => (capped.has(i) ? 0 : x))
    const freeTotal = free.reduce((a, b) => a + b, 0)
    if (freeTotal <= 1e-12) break // everyone at the cap: the rest stays in cash
    free.forEach((x, i) => { if (x > 0) w[i] += (excess * x) / freeTotal })
  }
  const weights = Object.fromEntries(chosen.map((s, i) => [s.symbol, w[i]]))
  return { weights, cash: Math.max(0, 1 - w.reduce((a, b) => a + b, 0)) }
}

// ------------------------------------------------------------------ ridge and logistic regression

/** Illustrative least-squares coefficients (% per standard deviation) of four standardized features. */
export const sampleOlsCoefficients: { feature: string; beta: number }[] = [
  { feature: 'momentum', beta: 0.4 }, { feature: 'volatility', beta: -0.25 }, { feature: 'drawdown', beta: 0.15 }, { feature: 'volume', beta: -0.08 },
]

/**
 * Ridge with the app's penalty λ·n on uncorrelated standardized features (ZᵀZ = nI): β_ridge = β_OLS / (1 + λ).
 * Correlated features shrink less evenly – the figure says so.
 */
export const ridgeShrink = (beta: number, lambda: number) => beta / (1 + lambda)

export const logistic = (x: number) => 1 / (1 + Math.exp(-x))

// ------------------------------------------------------------------ sample data and trees (random forest, gradient boosting)

/** Deterministic pseudo-random numbers (mulberry32): the same seed always gives the same sample data. */
export function seededRandom(seed: number): () => number {
  let t = seed >>> 0
  return () => {
    t = (t + 0x6d2b79f5) >>> 0
    let r = Math.imul(t ^ (t >>> 15), 1 | t)
    r = (r + Math.imul(r ^ (r >>> 7), 61 | r)) ^ r
    return ((r ^ (r >>> 14)) >>> 0) / 4294967296
  }
}

const gaussian = (rand: () => number) => Math.sqrt(-2 * Math.log(Math.max(rand(), 1e-12))) * Math.cos(2 * Math.PI * rand())

export interface Point { x: number; y: number }

/** The hidden pattern of the tree demos: a smooth, non-linear relation between one feature and the outcome. */
export const truePattern = (x: number) => Math.sin(2 * Math.PI * x) * 0.8 + 0.6 * x

/** Noisy sample points around `truePattern` (one feature in [0, 1]); training and test sets use different seeds. */
export function samplePoints(n: number, seed: number, noise = 0.45): Point[] {
  const rand = seededRandom(seed)
  return Array.from({ length: n }, () => {
    const x = rand()
    return { x, y: truePattern(x) + noise * gaussian(rand) }
  })
}

export type Tree = { split: number; left: Tree; right: Tree } | { value: number }

/** A regression tree on one feature: splits that minimize squared error, up to `depth`, at least `minLeaf` points per leaf. */
export function fitTree(points: Point[], depth: number, minLeaf = 4): Tree {
  const mean = points.reduce((s, p) => s + p.y, 0) / Math.max(1, points.length)
  if (depth === 0 || points.length < 2 * minLeaf) return { value: mean }
  const sorted = [...points].sort((a, b) => a.x - b.x)
  const total = sorted.reduce((s, p) => s + p.y, 0)
  let best = { sse: Infinity, at: -1 }
  let leftSum = 0
  let leftSq = 0
  const totalSq = sorted.reduce((s, p) => s + p.y * p.y, 0)
  for (let i = 0; i < sorted.length - 1; i++) {
    leftSum += sorted[i].y
    leftSq += sorted[i].y ** 2
    const nl = i + 1
    const nr = sorted.length - nl
    if (nl < minLeaf || nr < minLeaf || sorted[i].x === sorted[i + 1].x) continue
    const sse = leftSq - leftSum ** 2 / nl + (totalSq - leftSq) - (total - leftSum) ** 2 / nr
    if (sse < best.sse) best = { sse, at: i }
  }
  if (best.at < 0) return { value: mean }
  const split = (sorted[best.at].x + sorted[best.at + 1].x) / 2
  return { split, left: fitTree(sorted.slice(0, best.at + 1), depth - 1, minLeaf), right: fitTree(sorted.slice(best.at + 1), depth - 1, minLeaf) }
}

export function predictTree(tree: Tree, x: number): number {
  let node = tree
  while ('split' in node) node = x <= node.split ? node.left : node.right
  return node.value
}

/** Trees on bootstrap samples (drawn with replacement), as in a random forest; the forest predicts their average. */
export function fitForest(points: Point[], trees: number, depth: number, seed: number): Tree[] {
  const rand = seededRandom(seed)
  return Array.from({ length: trees }, () => fitTree(Array.from({ length: points.length }, () => points[Math.floor(rand() * points.length)]), depth))
}

export const predictForest = (forest: Tree[], count: number, x: number) =>
  forest.slice(0, count).reduce((s, t) => s + predictTree(t, x), 0) / Math.max(1, Math.min(count, forest.length))

/** Gradient boosting with squared error: start at the mean, each tree fits the residuals and is added × the learning rate. */
export function fitBoosting(points: Point[], steps: number, rate: number, depth: number): { base: number; trees: Tree[] } {
  const base = points.reduce((s, p) => s + p.y, 0) / points.length
  const current = points.map(() => base)
  const trees: Tree[] = []
  for (let m = 0; m < steps; m++) {
    const tree = fitTree(points.map((p, i) => ({ x: p.x, y: p.y - current[i] })), depth, 3)
    trees.push(tree)
    points.forEach((p, i) => { current[i] += rate * predictTree(tree, p.x) })
  }
  return { base, trees }
}

export const predictBoosting = (model: { base: number; trees: Tree[] }, steps: number, rate: number, x: number) =>
  model.trees.slice(0, steps).reduce((s, t) => s + rate * predictTree(t, x), model.base)

export const meanSquaredError = (points: Point[], predict: (x: number) => number) =>
  points.reduce((s, p) => s + (p.y - predict(p.x)) ** 2, 0) / Math.max(1, points.length)

// ------------------------------------------------------------------ strategy comparison: walk-forward and costs

/** Sixty sample monthly gross returns (seeded), shared by both strategies so only turnover and costs differ. */
export function sampleMonthlyReturns(months = 60, seed = 7): number[] {
  const rand = seededRandom(seed)
  return Array.from({ length: months }, () => 0.007 + 0.04 * gaussian(rand))
}

/** Growth of 100 when every month pays `turnover × cost` on top of the gross return (cost in basis points per traded value). */
export function growthAfterCosts(gross: number[], turnover: number, costBps: number): number[] {
  let value = 100
  return [100, ...gross.map((r) => (value *= 1 + r - turnover * (costBps / 10_000)))]
}
