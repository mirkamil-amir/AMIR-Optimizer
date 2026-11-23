import { date, money, num, pct } from './format'
import type { FundamentalMetric, StockFundamentals } from './types'

export type FundamentalKey = 'marketCap' | 'ebitEbitda' | 'roic' | 'peRatio' | 'freeCashFlow' | 'revenue' | 'revenueGrowth'

/** Column order of the stock list: revenue first, right after the charts (owner, 2026-11-23). */
export const FUNDAMENTAL_KEYS: FundamentalKey[] = ['revenue', 'revenueGrowth', 'marketCap', 'ebitEbitda', 'roic', 'peRatio', 'freeCashFlow']

/** Column header and the explanation shown on it. */
export const FUNDAMENTAL_COLUMNS: Record<FundamentalKey, { header: string; explanation: string }> = {
  marketCap: {
    header: 'CAP',
    explanation: 'Total market capitalization: share price × shares outstanding, as last published by the source.',
  },
  ebitEbitda: {
    header: 'EBIT/EBITDA',
    explanation:
      'Earnings before interest and taxes / earnings before interest, taxes, depreciation and amortization, latest fiscal year. Banks and insurers report neither (—).',
  },
  roic: {
    header: 'ROIC',
    explanation:
      'Return on invested capital, latest fiscal year: EBIT × (1 − tax rate) ÷ average invested capital (debt + equity) at the start and end of the year. Not meaningful for banks and insurers, so shown as —.',
  },
  peRatio: {
    header: 'PE',
    explanation: 'Price/earnings ratio: share price ÷ earnings per share of the trailing twelve months, as published by the source. Not shown when earnings are negative.',
  },
  freeCashFlow: {
    header: 'FCF',
    explanation: 'Free cash flow of the latest fiscal year: operating cash flow − capital expenditure.',
  },
  revenue: {
    header: 'Revenue',
    explanation: 'Revenue (sales) of the latest fiscal year.',
  },
  revenueGrowth: {
    header: 'Revenue growth',
    explanation: 'Revenue of the latest fiscal year compared with the year before.',
  },
}

/**
 * Converts amounts into the list's price currency with the latest reference rate (a comparison, like the converted price
 * column). `rate(from)` returns units of `from` per one unit of `to`, or null when there is no rate.
 */
export interface AmountConversion {
  to: string
  rate: (from: string) => { rate: number; date: string | null } | null
}

export interface FundamentalCell {
  text: string
  /** Hover text: the basis and period, the conversion, or why there is no value. */
  title: string | undefined
  /** Signed values are coloured; muted when there is no value. */
  tone: 'positive' | 'negative' | 'neutral' | 'muted'
}

interface Amount {
  text: string
  note: string | null
}

/** What a stock-list cell shows for one metric; "…" while the figures are being fetched, "—" with the reason when there are none. */
export function fundamentalCell(f: StockFundamentals | undefined, key: FundamentalKey, loading: boolean, fx?: AmountConversion): FundamentalCell {
  if (!f) return { text: loading ? '…' : '—', title: loading ? 'Loading company figures' : undefined, tone: 'muted' }
  if (f.status === 'Pending') return { text: '…', title: f.message ?? 'Company figures are being retrieved.', tone: 'muted' }

  if (key === 'ebitEbitda') {
    const ebit = f.ebit
    const ebitda = f.ebitda
    if (ebit?.value == null && ebitda?.value == null) return unavailable(ebit ?? ebitda, f)
    const a = ebit?.value != null ? convertAmount(ebit.value, ebit.currency, fx) : null
    const b = ebitda?.value != null ? convertAmount(ebitda.value, ebitda.currency, fx) : null
    const notes = [
      `EBIT / EBITDA, fiscal year ending ${date((ebit?.value != null ? ebit : ebitda)!.asOf)}.`,
      ebit?.value == null ? ebit?.limitation : null,
      ebitda?.value == null ? ebitda?.limitation : null,
      a?.note ?? b?.note,
    ]
    return finish(f, `${a?.text ?? '—'}/${b?.text ?? '—'}`, notes, tone(ebit?.value ?? ebitda!.value!))
  }

  const m: FundamentalMetric | null = f[key]
  if (!m || m.value === null) return unavailable(m, f)
  const period = key === 'marketCap' || key === 'peRatio' ? `as of ${date(m.asOf)}` : `fiscal year ending ${date(m.asOf)}`
  if (key === 'roic' || key === 'revenueGrowth') return finish(f, pct(m.value, 1), [`${m.basis}, ${period}.`], tone(m.value))
  if (key === 'peRatio') return finish(f, num(m.value, 1), [`${m.basis}, ${period}.`], 'neutral')
  const a = convertAmount(m.value, m.currency, fx)
  return finish(f, a.text, [`${m.basis}, ${period}.`, a.note], key === 'freeCashFlow' ? tone(m.value) : 'neutral')
}

function unavailable(m: FundamentalMetric | null | undefined, f: StockFundamentals): FundamentalCell {
  return { text: '—', title: m?.limitation ?? f.message ?? undefined, tone: 'muted' }
}

function finish(f: StockFundamentals, text: string, notes: (string | null | undefined)[], t: FundamentalCell['tone']): FundamentalCell {
  const all = [...notes, f.isSimulated ? 'SIMULATED figures.' : null, f.message].filter(Boolean)
  return { text: f.isSimulated ? `${text} (sim.)` : text, title: all.join(' '), tone: t }
}

const tone = (v: number): FundamentalCell['tone'] => (v > 0 ? 'positive' : v < 0 ? 'negative' : 'neutral')

/** An amount in the price currency when a rate exists, otherwise in the company's currency with the reason. */
export function convertAmount(value: number, currency: string | null, fx?: AmountConversion): Amount {
  if (!fx || !currency || currency === fx.to) return { text: compactMoney(value, currency), note: null }
  const r = fx.rate(currency)
  if (!r || !(r.rate > 0)) return { text: compactMoney(value, currency), note: `No ${currency}→${fx.to} rate available, so shown in ${currency}.` }
  return {
    text: compactMoney(value / r.rate, fx.to),
    note: `Converted from ${compactMoney(value, currency)} at the ${currency}/${fx.to} reference rate${r.date ? ` of ${date(r.date)}` : ''} (latest rate, a comparison only).`,
  }
}

/** Compact amount with its currency ("$5.1T", "€62.7B", "GEL 4.3B"); a code the browser does not know is shown as text. */
function compactMoney(value: number, currency: string | null): string {
  const plain = () => new Intl.NumberFormat('en-US', { notation: 'compact', maximumFractionDigits: 1 }).format(value)
  if (!currency) return plain()
  try {
    return money(value, currency, { compact: true })
  } catch {
    return `${currency} ${plain()}`
  }
}

/** Years between the first and last stored price, one decimal ("5.0 years"); null without both dates. */
export function historyYears(firstDate: string | null, lastDate: string | null): string | null {
  if (!firstDate || !lastDate) return null
  const days = (Date.parse(lastDate) - Date.parse(firstDate)) / 86_400_000
  if (!Number.isFinite(days) || days < 0) return null
  const years = days / 365.25
  return `${years.toFixed(1)} ${years.toFixed(1) === '1.0' ? 'year' : 'years'}`
}
