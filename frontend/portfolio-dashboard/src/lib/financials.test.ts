import { describe, expect, it } from 'vitest'
import { cellTitle, filledPeriods, financialValue, periodHeader, periodTitle } from './financials'
import type { FinancialRow } from './types'

describe('financials formatting', () => {
  it('labels fiscal years and quarters by their period end', () => {
    expect(periodHeader('2025-09-27', 'FY')).toBe('FY 2025')
    expect(periodHeader('2026-09-30', 'Q')).toBe('Sep 2026')
    expect(periodTitle('2026-03-31', 'FY')).toBe('Fiscal year ending Mar 31, 2026')
    expect(periodTitle('2026-09-30', 'Q')).toBe('Fiscal quarter ending Sep 30, 2026')
  })

  it('formats each kind of row and shows a dash without a value', () => {
    expect(financialValue('amount', 416_161_000_000, 'USD')).toBe('$416.2B')
    expect(financialValue('amount', -12_500_000, 'EUR')).toBe('-€12.5M')
    expect(financialValue('perShare', 7.456, 'USD')).toBe('USD 7.46')
    expect(financialValue('shares', 14_840_390_000, null)).toBe('14.84B')
    expect(financialValue('ratio', 0.2512, null)).toBe('25.1%')
    expect(financialValue('multiple', 1.234, null)).toBe('1.2×')
    expect(financialValue('amount', null, 'USD')).toBe('—')
  })

  it('leaves out periods in which the statement has neither a value nor a reason', () => {
    const row = (values: (number | null)[], notes: (string | null)[] = values.map(() => null)): FinancialRow => ({ key: 'x', label: 'x', kind: 'amount', currency: 'USD', values, notes })
    expect(filledPeriods({ key: 'income', title: 'Income statement', rows: [row([1, null, null, 4]), row([null, 2, null, null])] })).toEqual([0, 1, 3])
    expect(filledPeriods({ key: 'ratios', title: 'Ratios', rows: [row([null, 1], ['Not meaningful.', null])] })).toEqual([0, 1])
    expect(filledPeriods({ key: 'ratios', title: 'Ratios', rows: [] })).toEqual([])
  })

  it('explains a missing cell with its reason or as not reported', () => {
    const row: FinancialRow = { key: 'debtEquity', label: 'Debt/equity', kind: 'multiple', currency: null, values: [null, null, 0.5], notes: ['Not meaningful: the denominator is negative.', null, null] }
    expect(cellTitle(row, 0)).toBe('Not meaningful: the denominator is negative.')
    expect(cellTitle(row, 1)).toBe('Not reported by the source for this period.')
    expect(cellTitle(row, 2)).toBeUndefined()
  })
})
