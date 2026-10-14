import type { StockQuote } from './types'

export type QuoteFlag = 'simulated' | 'stale' | 'cached'

export interface QuoteStatus {
  /** Short label of what the price is. Never "live" or "real-time": providers are delayed or end-of-day. */
  label: 'No price' | 'Delayed quote' | 'Last close'
  /** When the price applies: time of the last trade for delayed quotes, the trading date for closes. */
  when: string | null
  /** Additional honest qualifiers shown as badges. */
  flags: QuoteFlag[]
  /** Full sentence for tooltips and screen readers. */
  description: string
}

const timeFormat = new Intl.DateTimeFormat('en-US', { hour: '2-digit', minute: '2-digit' })
const dayFormat = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' })

/** Describes how trustworthy and recent a displayed price is (pure; `now` is injectable for tests). */
export function quoteStatus(quote: StockQuote, now: Date = new Date()): QuoteStatus {
  if (quote.kind === 'None' || quote.price === null) {
    return { label: 'No price', when: null, flags: [], description: quote.description }
  }
  const flags: QuoteFlag[] = []
  if (quote.isSimulated) flags.push('simulated')
  if (quote.isStale) flags.push('stale')
  if (quote.isCached) flags.push('cached')

  if (quote.kind === 'DelayedQuote' && quote.asOfUtc) {
    const asOf = new Date(quote.asOfUtc)
    const sameDay = asOf.toDateString() === now.toDateString()
    return {
      label: 'Delayed quote',
      when: sameDay ? timeFormat.format(asOf) : `${dayFormat.format(asOf)} ${timeFormat.format(asOf)}`,
      flags,
      description: quote.description,
    }
  }
  const date = quote.priceDate ? dayFormat.format(new Date(`${quote.priceDate}T00:00:00Z`)) : null
  return { label: 'Last close', when: date, flags, description: quote.description }
}

/** "3 min ago" style age of a timestamp (pure). */
export function age(iso: string | null, now: Date = new Date()): string {
  if (!iso) return 'never'
  const seconds = Math.max(0, (now.getTime() - new Date(iso).getTime()) / 1000)
  if (seconds < 60) return 'just now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)} h ago`
  return `${Math.floor(seconds / 86_400)} d ago`
}

/** Symbols on a page whose price should be requested from the backend (missing or older than the cache window). */
export function symbolsNeedingRefresh(quotes: StockQuote[]): string[] {
  return quotes.filter((q) => q.kind === 'None' || q.isCached).map((q) => q.symbol)
}
