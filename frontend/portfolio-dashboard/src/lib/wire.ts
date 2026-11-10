import { regionLabels } from './news'
import type { AttentionLevel, MarketArea, WireEntity, WireFeedName, WireSourceStatus, WireStory } from './types'

/** The five Wire tabs, in order; the value is the API feed name. */
export const wireTabs: { value: WireFeedName; label: string; question: string }[] = [
  { value: 'latest', label: 'Latest', question: 'What happened recently, everywhere?' },
  { value: 'watchlist', label: 'My watchlist', question: 'What happened to what I follow?' },
  { value: 'companies', label: 'Company news', question: 'What is happening at companies?' },
  { value: 'markets', label: 'Market-wide', question: 'What is happening across markets?' },
  { value: 'macro', label: 'Macroeconomic', question: 'What is happening in the economy and policy?' },
]

/** Server default window of each feed (hours), shown as "Default (…)" so the page never hides what it asks for. */
export const defaultWindowHours: Record<WireFeedName, number> = { latest: 48, watchlist: 168, companies: 168, markets: 72, macro: 720 }

export const windowOptions: { hours: number; label: string }[] = [
  { hours: 1, label: 'Last hour' },
  { hours: 24, label: 'Last 24 hours' },
  { hours: 48, label: 'Last 48 hours' },
  { hours: 72, label: 'Last 3 days' },
  { hours: 168, label: 'Last 7 days' },
  { hours: 720, label: 'Last 30 days' },
  { hours: 2160, label: 'Last 90 days' },
]

export function windowLabel(hours: number): string {
  return windowOptions.find((o) => o.hours === hours)?.label ?? `Last ${hours} hours`
}

/** Attention choices per feed: Latest is Medium and High by definition. */
export function attentionOptions(feed: WireFeedName): { value: AttentionLevel | ''; label: string }[] {
  return feed === 'latest'
    ? [{ value: '', label: 'Medium and High' }, { value: 'High', label: 'High only' }]
    : [{ value: '', label: 'All attention levels' }, { value: 'Medium', label: 'Medium and High' }, { value: 'High', label: 'High only' }]
}

export const areaLabels: Record<MarketArea, string> = {
  Equities: 'Equities',
  Rates: 'Rates',
  Fx: 'Currencies',
  Commodities: 'Commodities',
  Volatility: 'Volatility',
}

export function regionLabel(code: string | null): string | null {
  if (!code) return null
  return regionLabels[code] ?? code
}

/** "HH:MM UTC" – Wire states times in UTC next to the relative label, so reports from several zones line up. */
export function utcTime(iso: string): string {
  const d = new Date(iso)
  return `${String(d.getUTCHours()).padStart(2, '0')}:${String(d.getUTCMinutes()).padStart(2, '0')} UTC`
}

/**
 * One line for the page header: when the newest source last succeeded and how many groups are delayed or unavailable.
 * Never says "live": it reports the stored fetch times.
 */
export function freshnessLine(sources: WireSourceStatus[]): string {
  if (sources.length === 0) return 'No news source is configured'
  const last = sources.map((s) => s.lastSuccessUtc).filter((t): t is string => !!t).sort().at(-1)
  const delayed = sources.filter((s) => s.status === 'Delayed').length
  const unavailable = sources.filter((s) => s.status === 'Unavailable').length
  const parts = [last ? `Sources updated ${utcTime(last)}` : 'Sources not fetched yet']
  if (delayed > 0) parts.push(`${delayed} delayed`)
  if (unavailable > 0) parts.push(`${unavailable} unavailable`)
  return parts.join(' · ')
}

/** "3 sources" or the single publisher, for the collapsed source line of a story. */
export function sourcesLabel(story: Pick<WireStory, 'publisher' | 'sources' | 'articleCount'>): string {
  const publishers = new Set(story.sources.map((s) => s.publisher))
  if (story.articleCount <= 1) return story.publisher
  return publishers.size > 1 ? `${story.publisher} and ${publishers.size - 1} more · ${story.articleCount} reports` : `${story.publisher} · ${story.articleCount} reports`
}

/** Benchmarks shown in the side panel (stored closes with provenance; seeded in every catalogue). */
export const wireBenchmarks = ['SPY', '^GDAXI', '^FTSE', '^N225', '^FCHI']

/** The feed that lists an entity's stories: central banks and statistics offices in Macroeconomic, market instruments in Market-wide. */
export function entityHref(entity: Pick<WireEntity, 'key' | 'kind'>): string {
  const view = entity.kind === 'CentralBank' || entity.kind === 'StatisticsOffice' ? 'macro' : 'markets'
  return `/news?view=${view}&entity=${encodeURIComponent(entity.key)}`
}
