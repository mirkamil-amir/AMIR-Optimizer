import { describe, expect, it } from 'vitest'
import { countByType, describeCatalogue, describeNotTracked, describeSymbols, notTrackedReason, notTrackedTotal, trackedTotal } from './instrument-counts'
import type { AssetCoverage } from './types'

const row = (symbol: string, assetType: AssetCoverage['assetType'], isEnabled: boolean) => ({ symbol, assetType, isEnabled })

// A catalogue shaped like the one that showed "2,158 Stocks & ETFs" next to a "1,918 stocks" download.
const catalogue = [
  row('AAPL', 'Equity', true), row('SAP.DE', 'Equity', true), row('NEW.DE', 'Equity', false),
  row('SPY', 'Etf', true), row('EWG', 'Etf', false),
  row('^GSPC', 'Index', true),
]

describe('instrument counts', () => {
  it('splits the catalogue by type and by tracking', () => {
    expect(countByType(catalogue)).toEqual([
      { type: 'Equity', tracked: 2, notTracked: 1 },
      { type: 'Etf', tracked: 1, notTracked: 1 },
      { type: 'Index', tracked: 1, notTracked: 0 },
    ])
    expect(trackedTotal(catalogue)).toBe(4)
  })

  it('labels how many are not tracked and leaves out zero', () => {
    expect(describeCatalogue(catalogue.filter((c) => c.assetType !== 'Index')))
      .toBe('Stocks: 2 tracked · 1 not tracked · ETFs: 1 tracked · 1 not tracked')
    expect(describeCatalogue(catalogue.filter((c) => c.assetType === 'Index'))).toBe('Indexes: 1 tracked')
    expect(describeCatalogue([])).toBe('')
  })

  it('describes a download job by type, so indexes are never called stocks', () => {
    // The scheduled refresh takes every tracked instrument of every type.
    const job = catalogue.filter((c) => c.isEnabled).map((c) => c.symbol)
    expect(describeSymbols(job, catalogue)).toBe('2 stocks, 1 ETF, 1 index')
    expect(describeSymbols(['AAPL', 'GONE'], catalogue)).toBe('1 stock, 1 other instrument')
  })

  it('counts the instruments that are not tracked', () => {
    expect(notTrackedTotal(catalogue)).toBe(2)
    expect(trackedTotal(catalogue) + notTrackedTotal(catalogue)).toBe(catalogue.length)
  })

  it('says why an instrument is not tracked', () => {
    expect(notTrackedReason({ isEnabled: true, observations: 0, lastSyncSuccess: null })).toBeNull()
    expect(notTrackedReason({ isEnabled: false, observations: 0, lastSyncSuccess: null })).toBe('never-downloaded')
    expect(notTrackedReason({ isEnabled: false, observations: 0, lastSyncSuccess: false })).toBe('no-prices')
    expect(notTrackedReason({ isEnabled: false, observations: 0, lastSyncSuccess: true })).toBe('no-prices')
    expect(notTrackedReason({ isEnabled: false, observations: 250, lastSyncSuccess: true })).toBe('switched-off')
  })

  it('explains the not tracked instruments by reason and leaves out zero counts', () => {
    const rows = [
      { isEnabled: true, observations: 500, lastSyncSuccess: true },
      { isEnabled: false, observations: 0, lastSyncSuccess: null },
      { isEnabled: false, observations: 0, lastSyncSuccess: null },
      { isEnabled: false, observations: 0, lastSyncSuccess: false },
    ]
    const text = describeNotTracked(rows)!
    expect(text).toMatch(/^3 not tracked: 2 never downloaded/)
    expect(text).toContain('1 a download was tried but brought no prices')
    expect(text).not.toContain('administrator')
    expect(describeNotTracked([{ isEnabled: true, observations: 1, lastSyncSuccess: true }])).toBeNull()
  })
})
