/**
 * Worked examples for the strategy guide, computed (not typed in) so every number shown to students is reproducible.
 * Two illustrative stocks with round-number assumptions – sample values, not market data.
 */
export const twoAssets = {
  a: { name: 'Stock A (steady)', expectedReturn: 0.08, volatility: 0.2 },
  b: { name: 'Stock B (growth)', expectedReturn: 0.12, volatility: 0.3 },
  correlation: 0.2,
  riskFree: 0.03,
}

export interface TwoAssetPortfolio {
  weightA: number
  weightB: number
  expectedReturn: number
  volatility: number
  sharpe: number
}

const { a, b, correlation: rho, riskFree } = twoAssets
const covAB = rho * a.volatility * b.volatility

export function portfolio(weightA: number): TwoAssetPortfolio {
  const wB = 1 - weightA
  const variance = weightA ** 2 * a.volatility ** 2 + wB ** 2 * b.volatility ** 2 + 2 * weightA * wB * covAB
  const expectedReturn = weightA * a.expectedReturn + wB * b.expectedReturn
  const volatility = Math.sqrt(variance)
  return { weightA, weightB: wB, expectedReturn, volatility, sharpe: (expectedReturn - riskFree) / volatility }
}

/** Global minimum variance: w_A = (σ_B² − ρσ_Aσ_B) / (σ_A² + σ_B² − 2ρσ_Aσ_B), clipped to long-only. */
export function minimumVariance(): TwoAssetPortfolio {
  const w = (b.volatility ** 2 - covAB) / (a.volatility ** 2 + b.volatility ** 2 - 2 * covAB)
  return portfolio(Math.min(1, Math.max(0, w)))
}

/** Tangency (maximum Sharpe): w ∝ Σ⁻¹(μ − r_f), normalized to sum to 1. */
export function maximumSharpe(): TwoAssetPortfolio {
  const ea = a.expectedReturn - riskFree
  const eb = b.expectedReturn - riskFree
  const varA = a.volatility ** 2
  const varB = b.volatility ** 2
  // Σ⁻¹ e up to the common factor 1/det.
  const xa = varB * ea - covAB * eb
  const xb = -covAB * ea + varA * eb
  return portfolio(Math.min(1, Math.max(0, xa / (xa + xb))))
}

/** Lowest-variance mix with expected return ≥ target (binding when the target exceeds the GMV return). */
export function targetReturn(target: number): TwoAssetPortfolio {
  const gmv = minimumVariance()
  if (gmv.expectedReturn >= target) return gmv
  return portfolio(Math.min(1, Math.max(0, (b.expectedReturn - target) / (b.expectedReturn - a.expectedReturn))))
}

/** Highest expected return with volatility ≤ limit: more of the higher-return stock until the limit binds. */
export function maximumReturnForRisk(limit: number): TwoAssetPortfolio | null {
  const gmv = minimumVariance()
  if (gmv.volatility > limit) return null
  // Along the efficient branch (w_A from GMV down to 0) volatility rises monotonically: bisection.
  let lo = 0
  let hi = gmv.weightA
  if (portfolio(0).volatility <= limit) return portfolio(0)
  for (let i = 0; i < 60; i++) {
    const mid = (lo + hi) / 2
    if (portfolio(mid).volatility > limit) lo = mid
    else hi = mid
  }
  return portfolio(hi)
}

export const equalWeight = (): TwoAssetPortfolio => portfolio(0.5)

/** Logistic function used by logistic regression: probability = 1 / (1 + e^(−score)). */
export const sigmoid = (x: number) => 1 / (1 + Math.exp(-x))
