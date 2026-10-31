import type { CurrencyExposure } from './types'

/**
 * Human-readable rate in the app's convention (rate = units of `to` per one `from`): "1 USD = 0.9132 EUR".
 * Small rates are shown inverted as well so that JPY rates stay readable ("1 JPY = 0.0061 EUR · 1 EUR = 163.9 JPY").
 */
export function fxPhrase(rate: number | null | undefined, from: string, to: string): string {
  if (rate === null || rate === undefined || !Number.isFinite(rate) || rate <= 0) return '—'
  if (from === to) return 'same currency'
  const direct = `1 ${from} = ${formatRate(rate)} ${to}`
  return rate < 0.1 ? `${direct} · 1 ${to} = ${formatRate(1 / rate)} ${from}` : direct
}

function formatRate(x: number): string {
  const digits = x >= 100 ? 2 : x >= 1 ? 4 : 6
  return new Intl.NumberFormat('en-US', { maximumFractionDigits: digits, minimumFractionDigits: Math.min(digits, 2) }).format(x)
}

/** Exposure rows sorted by weight; cash rows ("EUR cash") last among equals. Weights are fractions of total value. */
export function exposureRows(items: CurrencyExposure[] | null | undefined): CurrencyExposure[] {
  return [...(items ?? [])].filter((e) => e.weight > 0).sort((a, b) => b.weight - a.weight || a.currency.localeCompare(b.currency))
}

/** Currencies that occur in a selection or holdings other than the base currency. */
export function foreignCurrencies(currencies: (string | null | undefined)[], base: string): string[] {
  return [...new Set(currencies.filter((c): c is string => !!c && c !== base))].sort()
}

/** Amount converted with a rate in the app convention (units of target per one source); null when either is invalid. */
export function convertAmount(amount: number | null | undefined, rate: number | null | undefined): number | null {
  if (amount === null || amount === undefined || !Number.isFinite(amount)) return null
  if (rate === null || rate === undefined || !Number.isFinite(rate) || rate <= 0) return null
  return amount * rate
}

/** Rate from `from` to `to` derived from a board quoted against `base` (rates = units per one base). */
export function crossFromBoard(rates: Record<string, number | null | undefined>, base: string, from: string, to: string): number | null {
  if (from === to) return 1
  const perBase = (c: string) => (c === base ? 1 : rates[c] ?? null)
  const f = perBase(from)
  const t = perBase(to)
  if (!f || !t || f <= 0 || t <= 0) return null
  return t / f
}

/** Currencies shown by default on the rate board and listed first in the drop-downs. */
export const majorCurrencies = ['USD', 'EUR', 'GBP', 'JPY', 'CHF', 'CAD', 'AUD', 'CNY', 'SEK', 'NOK', 'TRY']

export function converterCurrencies(base: string, supported: string[] | undefined): string[] {
  const set = new Set(supported ?? majorCurrencies)
  return [base, ...majorCurrencies.filter((c) => c !== base && set.has(c))]
}

/**
 * Drop-down groups: `first` (e.g. the portfolio currency) and the supported majors, then every other supported currency
 * alphabetically. `exclude` is left out (e.g. the board's own base).
 */
export function currencyGroups(supported: string[] | undefined, first?: string, exclude?: string): { major: string[]; more: string[] } {
  const all = [...new Set([...(first ? [first] : []), ...(supported ?? [])])].filter((c) => c !== exclude)
  const major = [...(first ? [first] : []), ...majorCurrencies].filter((c, i, a) => all.includes(c) && a.indexOf(c) === i)
  return { major, more: all.filter((c) => !major.includes(c)).sort() }
}

let names: Intl.DisplayNames | null | undefined
/** English currency name ("Turkish Lira"); the code itself when the runtime has no name for it. */
export function currencyName(code: string): string {
  if (names === undefined) {
    try { names = new Intl.DisplayNames(['en'], { type: 'currency' }) } catch { names = null }
  }
  try { return names?.of(code) ?? code } catch { return code }
}

/** A share price in another currency: price × the dated rate (target units per source unit); null without a rate. */
export function convertedPrice(price: number | null, from: string, to: string, rate: number | null): number | null {
  if (price === null) return null
  if (!from || from === to) return price
  return rate !== null && rate > 0 ? price * rate : null
}
