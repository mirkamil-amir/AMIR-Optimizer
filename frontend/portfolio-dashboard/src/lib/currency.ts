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
