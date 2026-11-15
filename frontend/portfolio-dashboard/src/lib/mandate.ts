import type { PilotFrequency, PilotMandate } from '@/lib/types'

// The Matrix Pilot mandate form's state and its conversion to and from a mandate (components/mandate-form.tsx).

export interface MandateFormState {
  universe: string[]; indexes: string[]; excluded: string[]
  maxPos: string; minPos: string; minHoldings: string; maxHoldings: string; minCash: string; maxCash: string
  maxTurnover: string; minTrade: string; maxVol: string; drawdown: string
  /** Chosen limits: key (sector name, ISO country, currency code) → percent as typed. */
  sectors: Record<string, string>; currencies: Record<string, string>; countries: Record<string, string>
  /** '' = the portfolio's benchmark. */
  benchmark: string
  frequency: PilotFrequency; effort: string
}

export const MAX_INDEXES = 10
export const MAX_SYMBOLS = 500
export const DEFAULT_LIMIT = '20'

const p100 = (v: number) => String(Math.round(v * 10000) / 100)
const percents = (r: Record<string, number>) => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, p100(v)]))

export function toForm(m: PilotMandate, frequency: PilotFrequency, effort: string): MandateFormState {
  return {
    universe: [...m.universe], indexes: [...m.indexes], excluded: [...m.excludedSymbols],
    maxPos: p100(m.maxPositionWeight), minPos: p100(m.minPositionWeight), minHoldings: String(m.minHoldings), maxHoldings: String(m.maxHoldings),
    minCash: p100(m.minCashWeight), maxCash: p100(m.maxCashWeight), maxTurnover: p100(m.maxTurnover), minTrade: String(m.minTradeValue),
    maxVol: m.maxVolatility == null ? '' : p100(m.maxVolatility), drawdown: p100(m.drawdownPauseThreshold),
    sectors: percents(m.sectorLimits), currencies: percents(m.currencyLimits), countries: percents(m.countryLimits),
    benchmark: m.benchmark ?? '', frequency, effort,
  }
}

/** Reads the form; the server validates the mandate again (and rejects, never repairs). */
export function fromForm(f: MandateFormState): { mandate: PilotMandate } | { error: string } {
  const frac = (s: string, name: string) => {
    const v = Number(s.replace('%', '').replace(',', '.').trim())
    if (s.trim() === '' || !Number.isFinite(v)) throw new Error(`${name} must be a number (percent).`)
    return v / 100
  }
  const whole = (s: string, name: string) => {
    const v = Number(s.trim())
    if (s.trim() === '' || !Number.isFinite(v)) throw new Error(`${name} must be a number.`)
    return v
  }
  const limits = (r: Record<string, string>, name: string) => Object.fromEntries(Object.entries(r).map(([k, v]) => [k, frac(v, `${name} for ${k}`)]))
  try {
    return {
      mandate: {
        universe: f.universe, indexes: f.indexes, excludedSymbols: f.excluded,
        maxPositionWeight: frac(f.maxPos, 'Maximum position'), minPositionWeight: frac(f.minPos, 'Minimum position'),
        minHoldings: whole(f.minHoldings, 'Minimum holdings'), maxHoldings: whole(f.maxHoldings, 'Maximum holdings'),
        minCashWeight: frac(f.minCash, 'Minimum cash'), maxCashWeight: frac(f.maxCash, 'Maximum cash'),
        maxTurnover: frac(f.maxTurnover, 'Maximum turnover'), minTradeValue: whole(f.minTrade, 'Minimum trade value'),
        maxVolatility: f.maxVol.trim() ? frac(f.maxVol, 'Maximum volatility') : null, drawdownPauseThreshold: frac(f.drawdown, 'Drawdown pause'),
        sectorLimits: limits(f.sectors, 'Sector limit'), currencyLimits: limits(f.currencies, 'Currency limit'), countryLimits: limits(f.countries, 'Country limit'),
        benchmark: f.benchmark || null,
      },
    }
  } catch (e) {
    return { error: (e as Error).message }
  }
}

