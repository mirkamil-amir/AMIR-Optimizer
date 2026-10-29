import type { AssetCoverage } from './types'

/**
 * Instrument counts of Market Data, always split by type and by tracking: a single total of the catalogue mixes stocks
 * with ETFs and tracked instruments with ones that were never downloaded, so it cannot be compared with a download job
 * (which only takes tracked instruments, of every type). "Tracked" is `isEnabled` – the same rule the sync uses
 * (`MarketDataService.EnqueueSyncAsync`).
 */
export type InstrumentType = AssetCoverage['assetType']

export interface TypeCount { tracked: number; notTracked: number }

export const TYPE_ORDER: readonly InstrumentType[] = ['Equity', 'Etf', 'Index']

const NAMES: Record<InstrumentType, [one: string, many: string]> = {
  Equity: ['stock', 'stocks'],
  Etf: ['ETF', 'ETFs'],
  Index: ['index', 'indexes'],
}

export function typeName(type: InstrumentType, n: number) {
  return NAMES[type][n === 1 ? 0 : 1]
}

/** Tracked and not tracked instruments per type, in `TYPE_ORDER`; types without any instrument are left out. */
export function countByType(rows: readonly Pick<AssetCoverage, 'assetType' | 'isEnabled'>[]) {
  const counts = new Map<InstrumentType, TypeCount>()
  for (const r of rows) {
    const c = counts.get(r.assetType) ?? { tracked: 0, notTracked: 0 }
    if (r.isEnabled) c.tracked++
    else c.notTracked++
    counts.set(r.assetType, c)
  }
  return TYPE_ORDER.filter((t) => counts.has(t)).map((type) => ({ type, ...counts.get(type)! }))
}

export function trackedTotal(rows: readonly Pick<AssetCoverage, 'isEnabled'>[]) {
  return rows.filter((r) => r.isEnabled).length
}

/** "Stocks: 1,863 tracked · 262 not tracked · ETFs: 25 tracked · 8 not tracked" (a zero "not tracked" is left out). */
export function describeCatalogue(rows: readonly Pick<AssetCoverage, 'assetType' | 'isEnabled'>[]) {
  return countByType(rows).map(({ type, tracked, notTracked }) => {
    const name = typeName(type, 2)
    return `${name[0].toUpperCase()}${name.slice(1)}: ${tracked.toLocaleString()} tracked${notTracked ? ` · ${notTracked.toLocaleString()} not tracked` : ''}`
  }).join(' · ')
}

/**
 * What a download job contains: "1,863 stocks, 25 ETFs, 30 indexes". Symbols missing from the catalogue (e.g. just
 * removed) are counted as "other instruments" instead of being guessed.
 */
export function describeSymbols(symbols: readonly string[], catalogue: readonly Pick<AssetCoverage, 'symbol' | 'assetType'>[]) {
  const typeOf = new Map(catalogue.map((c) => [c.symbol, c.assetType]))
  const counts = new Map<InstrumentType | 'Other', number>()
  for (const s of symbols) {
    const t = typeOf.get(s) ?? 'Other'
    counts.set(t, (counts.get(t) ?? 0) + 1)
  }
  const parts = TYPE_ORDER.filter((t) => counts.has(t)).map((t) => `${counts.get(t)!.toLocaleString()} ${typeName(t, counts.get(t)!)}`)
  const other = counts.get('Other')
  if (other) parts.push(`${other.toLocaleString()} other instrument${other === 1 ? '' : 's'}`)
  return parts.join(', ')
}
