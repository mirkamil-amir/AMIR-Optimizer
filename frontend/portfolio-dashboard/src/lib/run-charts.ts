import type { Allocation } from '@/lib/types'

/**
 * Pure helpers for the charts of one Math Optimization run. They only reshape stored run data (no new estimates):
 * drawdowns of the stored in-sample curves, weights grouped by sector and the equal-weight comparison.
 */

/** Drawdown from the running peak of a cumulative-return series (0.1 = +10 %), e.g. −0.2 = 20 % below the peak. */
export function drawdownFromCumulative(cumulative: number[]): number[] {
  let peak = -Infinity
  return cumulative.map((c) => {
    const value = 1 + c
    peak = Math.max(peak, value)
    return peak > 0 ? value / peak - 1 : 0
  })
}

/** Weights summed per sector (unclassified stocks together), largest first; cash is not a sector. */
export function weightsBySector(allocation: Allocation[]): { sector: string; weight: number }[] {
  const sums = new Map<string, number>()
  for (const a of allocation) {
    if (a.weight <= 1e-6) continue
    const sector = a.sector ?? 'Unclassified'
    sums.set(sector, (sums.get(sector) ?? 0) + a.weight)
  }
  return [...sums].map(([sector, weight]) => ({ sector, weight })).sort((x, y) => y.weight - x.weight || x.sector.localeCompare(y.sector))
}

/** The weight every stock of the universe would get in an equal-weight portfolio with the same cash share. */
export function equalWeight(universeSize: number, investedShare: number): number {
  return universeSize > 0 ? investedShare / universeSize : 0
}
