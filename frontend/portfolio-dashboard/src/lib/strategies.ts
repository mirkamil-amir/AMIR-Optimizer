import type { DataCheckRequest, PortfolioStrategy, StrategyRef } from './types'

/** Stable key of a portfolio optimization for selects: `Mathematical:<id>` or `MachineLearning:<id>`. */
export const strategyKey = (s: { kind: StrategyRef['kind']; id: string }) => `${s.kind}:${s.id}`

/** The reference sent to the API for a select key (null when nothing or something unknown is chosen). */
export function strategyRef(key: string | null, strategies: PortfolioStrategy[]): StrategyRef | null {
  const s = strategies.find((x) => strategyKey(x) === key)
  return s ? { kind: s.kind, id: s.id } : null
}

/** A hybrid (ML screen + mean-variance weights) needs exactly one mathematical and one ML optimization. */
export function hybridPossible(a: PortfolioStrategy | undefined, b: PortfolioStrategy | undefined): boolean {
  return !!a && !!b && a.kind !== b.kind
}

/**
 * The prices and rates simulating `strategies` from `start` to `end` needs: their stocks and benchmark, from the start
 * minus the longest history any of them uses (lookback or ML training years).
 */
export function dataNeeded(strategies: PortfolioStrategy[], start: string, end: string | null, currency: string): DataCheckRequest {
  const days = Math.max(0, ...strategies.map((s) => s.historyDaysBeforeStart))
  const from = new Date(`${start}T00:00:00Z`)
  from.setUTCDate(from.getUTCDate() - days)
  return {
    symbols: [...new Set(strategies.flatMap((s) => s.universe))],
    benchmarkSymbol: strategies[0]?.benchmarkSymbol ?? null,
    from: from.toISOString().slice(0, 10),
    to: end,
    currency,
  }
}

/**
 * Start used for the data check when no start is entered: three years ago. The server's default is never earlier (an ML
 * test period starts later), so checking from here covers it.
 */
export function defaultStart(): string {
  const d = new Date()
  d.setUTCFullYear(d.getUTCFullYear() - 3)
  return d.toISOString().slice(0, 10)
}
