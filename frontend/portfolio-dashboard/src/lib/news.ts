import type { NewsArticle, NewsCategory, NewsScope } from './types'

export const categoryLabels: Record<NewsCategory, string> = {
  General: 'General',
  Earnings: 'Earnings & guidance',
  CorporateTransaction: 'M&A & corporate transactions',
  RegulatoryLegal: 'Regulatory & legal',
  CentralBank: 'Central banks & rates',
  Macroeconomic: 'Macroeconomic data',
  Geopolitical: 'Geopolitics & trade',
  CommoditiesEnergy: 'Commodities & energy',
  CurrenciesBonds: 'Currencies & bonds',
  MarketIndex: 'Markets & indices',
}

export const scopeLabels: Record<NewsScope, string> = {
  Company: 'Company news',
  Market: 'Market-wide',
  Macro: 'Macroeconomic',
}

export const regionLabels: Record<string, string> = {
  US: 'United States', EU: 'Euro area', DE: 'Germany', FR: 'France', NL: 'Netherlands', GB: 'United Kingdom', JP: 'Japan',
}

/** "3 h ago" for recent items, else the local date; never claims a time the provider did not supply. */
export function publishedLabel(iso: string, now: Date = new Date()): string {
  const t = new Date(iso)
  const minutes = Math.floor((now.getTime() - t.getTime()) / 60_000)
  if (minutes < 0) return t.toLocaleString('en-US', { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' })
  if (minutes < 60) return `${Math.max(1, minutes)} min ago`
  if (minutes < 24 * 60) return `${Math.floor(minutes / 60)} h ago`
  return t.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' })
}

/** Publisher plus other outlets that carried the same headline. */
export function sourceLine(a: Pick<NewsArticle, 'publisher' | 'alsoReportedBy'>): string {
  if (a.alsoReportedBy.length === 0) return a.publisher
  const others = a.alsoReportedBy.length === 1 ? a.alsoReportedBy[0] : `${a.alsoReportedBy.length} other publishers`
  return `${a.publisher} · also reported by ${others}`
}

/** Only absolute http(s) links are rendered as article links. */
export function safeExternalUrl(url: string): string | null {
  try {
    const u = new URL(url)
    return u.protocol === 'https:' || u.protocol === 'http:' ? u.toString() : null
  } catch {
    return null
  }
}
