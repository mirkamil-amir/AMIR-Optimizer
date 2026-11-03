import { indexName, regionOf, regions, type Region } from '@/lib/markets'
import type { Market } from '@/lib/types'

/**
 * What the stock list is narrowed to: any number of markets (ISO countries), indices and sectors. A stock matches when it
 * is in any chosen market OR any chosen index, and – if sectors are chosen – in one of them. Empty lists mean "all".
 * A whole region is simply all of its markets; an index of a chosen market is already included and never listed twice.
 */
export interface UniverseScope {
  countries: string[]
  indices: string[]
  sectors: string[]
}

export const emptyScope: UniverseScope = { countries: [], indices: [], sectors: [] }

export const isEmptyScope = (s: UniverseScope) => s.countries.length === 0 && s.indices.length === 0 && s.sectors.length === 0

/** Stocks and ETFs of a market, each once – the rows the chooser lists for it (index members summed only as a fallback). */
export function marketCount(m: Market) {
  return m.instruments || m.indices.filter((i) => !i.isHistorical).reduce((n, i) => n + i.members, 0)
}

export const marketsOf = (markets: Market[], region: Region) => markets.filter((m) => regionOf(m.country) === region)

/** Regions all of whose markets are chosen – shown as one chip/label instead of each market. */
export function wholeRegions(markets: Market[] | undefined, scope: UniverseScope): Region[] {
  if (!markets) return []
  return regions.filter((r) => {
    const inRegion = marketsOf(markets, r)
    return inRegion.length > 1 && inRegion.every((m) => scope.countries.includes(m.country))
  })
}

/** Each active part of the choice as a removable item (regions whole, then single markets, indices, sectors). */
export function scopeParts(markets: Market[] | undefined, scope: UniverseScope): { key: string; label: string; remove: UniverseScope }[] {
  const parts: { key: string; label: string; remove: UniverseScope }[] = []
  const whole = wholeRegions(markets, scope)
  const covered = new Set(whole.flatMap((r) => marketsOf(markets ?? [], r).map((m) => m.country)))
  for (const r of whole) {
    parts.push({ key: `region:${r}`, label: r, remove: { ...scope, countries: scope.countries.filter((c) => regionOf(c) !== r) } })
  }
  for (const c of scope.countries.filter((c) => !covered.has(c))) {
    parts.push({ key: `market:${c}`, label: markets?.find((m) => m.country === c)?.name ?? c, remove: { ...scope, countries: scope.countries.filter((x) => x !== c) } })
  }
  for (const i of scope.indices) {
    parts.push({ key: `index:${i}`, label: indexName(markets, i), remove: { ...scope, indices: scope.indices.filter((x) => x !== i) } })
  }
  for (const s of scope.sectors) {
    parts.push({ key: `sector:${s}`, label: s, remove: { ...scope, sectors: scope.sectors.filter((x) => x !== s) } })
  }
  return parts
}

/** Short title of the choice: "All markets", "Europe", "Germany, DAX 40 › Energy", or "5 markets, 2 indices › 3 sectors". */
export function describeScope(markets: Market[] | undefined, scope: UniverseScope) {
  const parts = scopeParts(markets, scope)
  const places = parts.filter((p) => !p.key.startsWith('sector:')).map((p) => p.label)
  const sectors = scope.sectors
  const placeText = places.length === 0 ? 'All markets'
    : places.length <= 3 ? places.join(', ')
      : [scope.countries.length && `${scope.countries.length} markets`, scope.indices.length && `${scope.indices.length} ${scope.indices.length === 1 ? 'index' : 'indices'}`].filter(Boolean).join(', ')
  if (sectors.length === 0) return placeText
  return `${placeText} › ${sectors.length <= 2 ? sectors.join(', ') : `${sectors.length} sectors`}`
}
