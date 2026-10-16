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

/** Currencies offered in the converter: the portfolio currency first, then the majors. */
export const majorCurrencies = ['USD', 'EUR', 'GBP', 'JPY', 'CHF', 'CAD', 'AUD', 'CNY', 'SEK', 'NOK']

export function converterCurrencies(base: string, supported: string[] | undefined): string[] {
  const set = new Set(supported ?? majorCurrencies)
  return [base, ...majorCurrencies.filter((c) => c !== base && set.has(c))]
}
