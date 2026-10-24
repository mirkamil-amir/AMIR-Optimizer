import type { DataCheck } from './types'

/** Ask only when something can actually be downloaded (or is still arriving); otherwise the analysis reports the gaps itself. */
export function shouldAsk(check: DataCheck): boolean {
  return !check.complete && (check.downloadable || check.downloading)
}

/** After a download: proceed when complete, wait while jobs or rates are still arriving, otherwise show what is still missing. */
export function nextStep(check: DataCheck, jobsDone: boolean): 'proceed' | 'wait' | 'incomplete' {
  if (check.complete) return 'proceed'
  if (!jobsDone || check.downloading) return 'wait'
  return 'incomplete'
}

/** Start date of the prices an analysis with a lookback of `lookbackDays` trading days needs before `start` (as the backtest loads them). */
export function lookbackStart(start: Date, lookbackDays: number): string {
  const d = new Date(start)
  d.setDate(d.getDate() - Math.ceil(lookbackDays * 1.5) - 10)
  return d.toISOString().slice(0, 10)
}

/** ISO date `years` calendar years before today (the start of an ML experiment's history). */
export function yearsAgo(years: number): string {
  const d = new Date()
  d.setFullYear(d.getFullYear() - years)
  return d.toISOString().slice(0, 10)
}
