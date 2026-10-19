import { describe, expect, it } from 'vitest'
import { axisDate, date, money, num, pct, qty, signClass } from './format'

describe('format', () => {
  it('formats fractions as percentages', () => {
    expect(pct(0.1234)).toBe('12.34%')
    expect(pct(0.05, 1, true)).toBe('+5.0%')
    expect(pct(-0.05, 0, true)).toBe('-5%')
  })

  it('renders missing or non-finite values as a dash, never NaN', () => {
    for (const v of [null, undefined, Number.NaN, Number.POSITIVE_INFINITY]) {
      expect(pct(v)).toBe('—')
      expect(money(v)).toBe('—')
      expect(num(v)).toBe('—')
      expect(qty(v)).toBe('—')
    }
  })

  it('formats money with currency and sign', () => {
    expect(money(1234.5)).toBe('$1,234.50')
    expect(money(10, 'USD', { signed: true })).toBe('+$10.00')
  })

  it('formats ISO dates without timezone shifts', () => {
    expect(date('2026-01-01')).toBe('Jan 1, 2026')
    expect(date('2026-10-09T23:59:59Z')).toBe('Oct 9, 2026')
    expect(date(null)).toBe('—')
  })

  it('chooses gain/loss classes by sign', () => {
    expect(signClass(1)).toBe('text-gain')
    expect(signClass(-1)).toBe('text-loss')
    expect(signClass(0)).toBe('text-foreground')
  })

  it('labels chart axes with the day on short spans, so a three-month chart does not repeat "Aug 26"', () => {
    expect(axisDate('2026-08-12', 92)).toBe('Aug 12')
    expect(axisDate('2026-08-12', 366)).toBe('Aug 26')
  })
})
