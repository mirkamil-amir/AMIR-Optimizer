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

export function notTrackedTotal(rows: readonly Pick<AssetCoverage, 'isEnabled'>[]) {
  return rows.filter((r) => !r.isEnabled).length
}

type ReasonRow = Pick<AssetCoverage, 'isEnabled' | 'observations' | 'lastSyncSuccess'>

/**
 * Why an instrument is not tracked, from what the catalogue knows about it. Index member lists add instruments untracked
 * (`UniverseService`); the first download that brings prices starts tracking (`MarketDataService.SyncAsync`), and only an
 * administrator can switch tracking off again (`UpdateAssetAsync`, stored prices are kept).
 */
export type NotTrackedReason = 'never-downloaded' | 'no-prices' | 'switched-off'

export function notTrackedReason(r: ReasonRow): NotTrackedReason | null {
  if (r.isEnabled) return null
  if (r.observations > 0) return 'switched-off'
  return r.lastSyncSuccess == null ? 'never-downloaded' : 'no-prices'
}

const REASONS: Record<NotTrackedReason, string> = {
  'never-downloaded': 'never downloaded – listed from an index\'s members, no prices fetched yet (Download fetches them and starts tracking)',
  'no-prices': 'a download was tried but brought no prices (the provider failed or has no history for the symbol)',
  'switched-off': 'switched off by an administrator (stored prices are kept but no longer refreshed)',
}

/**
 * "226 not tracked: 220 never downloaded – …; 5 a download was tried but …" – the reasons in a fixed order, zero
 * counts left out; null when everything is tracked.
 */
export function describeNotTracked(rows: readonly ReasonRow[]) {
  const counts = new Map<NotTrackedReason, number>()
  for (const r of rows) {
    const reason = notTrackedReason(r)
    if (reason) counts.set(reason, (counts.get(reason) ?? 0) + 1)
  }
  const total = [...counts.values()].reduce((a, b) => a + b, 0)
  if (!total) return null
  const parts = (Object.keys(REASONS) as NotTrackedReason[]).filter((k) => counts.has(k)).map((k) => `${counts.get(k)!.toLocaleString()} ${REASONS[k]}`)
  return `${total.toLocaleString()} not tracked: ${parts.join('; ')}.`
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
