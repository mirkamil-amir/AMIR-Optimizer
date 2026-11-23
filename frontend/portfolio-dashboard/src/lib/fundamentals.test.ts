import { describe, expect, it } from 'vitest'
import { fundamentalCell, historyYears, type AmountConversion } from './fundamentals'
import type { FundamentalMetric, StockFundamentals } from './types'

const metric = (value: number | null, currency: string | null, asOf = '2025-12-31', limitation: string | null = null): FundamentalMetric =>
  ({ value, currency, asOf: value === null ? null : asOf, basis: 'Basis', limitation })

const base: StockFundamentals = {
  symbol: 'ABC',
  status: 'Available',
  marketCap: metric(5.1e12, 'USD', '2026-11-17'),
  freeCashFlow: metric(-2.5e9, 'USD'),
  roic: metric(null, null, undefined, 'EBIT is not reported (usual for banks).'),
  revenueGrowth: metric(0.0625, null),
  ebit: metric(56e6, 'USD'),
  ebitda: metric(50e6, 'USD'),
  peRatio: metric(39.19, null, '2026-11-17'),
  revenue: metric(4.788e11, 'USD'),
  retrievedAtUtc: '2026-11-23T10:00:00Z',
  isSimulated: false,
  message: null,
}

// 1 USD = 0.92 EUR → 1.087 USD per EUR; GEL has no rate.
const toUsd: AmountConversion = { to: 'USD', rate: (from) => (from === 'EUR' ? { rate: 0.92, date: '2026-11-20' } : null) }

describe('fundamentalCell', () => {
  it('formats amounts compactly and ratios as percentages or plain numbers', () => {
    expect(fundamentalCell(base, 'marketCap', false, toUsd)).toMatchObject({ text: '$5.1T', tone: 'neutral' })
    expect(fundamentalCell(base, 'freeCashFlow', false).tone).toBe('negative')
    expect(fundamentalCell(base, 'revenueGrowth', false)).toMatchObject({ text: '6.3%', tone: 'positive' })
    expect(fundamentalCell(base, 'peRatio', false).text).toBe('39.2')
    expect(fundamentalCell(base, 'revenue', false).text).toBe('$478.8B')
    expect(fundamentalCell(base, 'marketCap', false).title).toContain('as of Nov 17, 2026')
  })

  it('shows EBIT and EBITDA in one cell separated by a slash', () => {
    expect(fundamentalCell(base, 'ebitEbitda', false, toUsd).text).toBe('$56M/$50M')
    expect(fundamentalCell({ ...base, ebit: metric(null, null, undefined, 'No EBIT.') }, 'ebitEbitda', false).text).toBe('—/$50M')
    const bank = fundamentalCell({ ...base, ebit: metric(null, null, undefined, 'No EBIT (banks).'), ebitda: metric(null, null) }, 'ebitEbitda', false)
    expect(bank).toEqual({ text: '—', title: 'No EBIT (banks).', tone: 'muted' })
  })

  it('converts amounts into the price currency with the latest rate, and says when there is none', () => {
    const eur = fundamentalCell({ ...base, marketCap: metric(174.5e9, 'EUR', '2026-11-17') }, 'marketCap', false, toUsd)
    expect(eur.text).toBe('$189.7B') // 174.5B ÷ 0.92
    expect(eur.title).toContain('Converted from €174.5B')
    expect(eur.title).toContain('Nov 20, 2026')
    const gel = fundamentalCell({ ...base, freeCashFlow: metric(1.856e9, 'GEL') }, 'freeCashFlow', false, toUsd)
    expect(gel.text).toMatch(/GEL\s?1\.9B/)
    expect(gel.title).toContain('No GEL→USD rate')
  })

  it('shows — with the reason when a metric is unavailable, never 0', () => {
    expect(fundamentalCell(base, 'roic', false)).toEqual({ text: '—', title: 'EBIT is not reported (usual for banks).', tone: 'muted' })
    expect(fundamentalCell({ ...base, status: 'NotApplicable', roic: null, message: 'Only for companies.' }, 'roic', false).title).toBe('Only for companies.')
  })

  it('marks pending, loading and simulated figures', () => {
    expect(fundamentalCell({ ...base, status: 'Pending' }, 'marketCap', false).text).toBe('…')
    expect(fundamentalCell(undefined, 'marketCap', true).text).toBe('…')
    expect(fundamentalCell(undefined, 'marketCap', false).text).toBe('—')
    const sim = fundamentalCell({ ...base, isSimulated: true }, 'marketCap', false)
    expect(sim.text).toBe('$5.1T (sim.)')
    expect(sim.title).toContain('SIMULATED')
  })
})

describe('historyYears', () => {
  it('gives the stored span in years with one decimal', () => {
    expect(historyYears('2021-11-22', '2026-11-20')).toBe('5.0 years')
    expect(historyYears('2025-05-20', '2026-11-20')).toBe('1.5 years')
    expect(historyYears('2025-11-20', '2026-11-20')).toBe('1.0 year')
    expect(historyYears(null, '2026-11-20')).toBeNull()
  })
})
