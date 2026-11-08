import { describe, expect, it } from 'vitest'
import { attentionOptions, defaultWindowHours, freshnessLine, regionLabel, sourcesLabel, utcTime, windowLabel, wireTabs } from './wire'
import type { WireSourceStatus } from './types'

const status = (s: Partial<WireSourceStatus>): WireSourceStatus => ({ group: 'Central banks', status: 'Fresh', lastSuccessUtc: null, problem: null, ...s })

describe('wire helpers', () => {
  it('has the five feeds in order, each with a default window', () => {
    expect(wireTabs.map((t) => t.value)).toEqual(['latest', 'watchlist', 'companies', 'markets', 'macro'])
    for (const t of wireTabs) expect(defaultWindowHours[t.value]).toBeGreaterThan(0)
  })

  it('offers Low attention everywhere but in Latest', () => {
    expect(attentionOptions('latest').map((o) => o.value)).toEqual(['', 'High'])
    expect(attentionOptions('macro').map((o) => o.value)).toEqual(['', 'Medium', 'High'])
  })

  it('labels windows, regions and UTC times', () => {
    expect(windowLabel(168)).toBe('Last 7 days')
    expect(windowLabel(5)).toBe('Last 5 hours')
    expect(regionLabel('EU')).toBe('Euro area')
    expect(regionLabel(null)).toBeNull()
    expect(utcTime('2026-11-08T09:05:00Z')).toBe('09:05 UTC')
  })

  it('summarises freshness without claiming live data', () => {
    expect(freshnessLine([])).toBe('No news source is configured')
    expect(freshnessLine([status({ lastSuccessUtc: '2026-11-08T19:53:00Z' }), status({ status: 'Delayed', lastSuccessUtc: '2026-11-08T10:00:00Z' }),
      status({ status: 'Unavailable' })])).toBe('Sources updated 19:53 UTC · 1 delayed · 1 unavailable')
    expect(freshnessLine([status({ status: 'Unavailable' })])).toBe('Sources not fetched yet · 1 unavailable')
    expect(freshnessLine([status({ lastSuccessUtc: 'x' })])).not.toMatch(/live/i)
  })

  it('names the lead publisher and how many reports and publishers a story has', () => {
    const src = (publisher: string) => ({ articleId: publisher, publisher, url: 'https://x.test', publishedAtUtc: '2026-11-08T09:00:00Z', isOfficial: false })
    expect(sourcesLabel({ publisher: 'A', articleCount: 1, sources: [src('A')] })).toBe('A')
    expect(sourcesLabel({ publisher: 'A', articleCount: 3, sources: [src('A'), src('B'), src('C')] })).toBe('A and 2 more · 3 reports')
    expect(sourcesLabel({ publisher: 'A', articleCount: 2, sources: [src('A'), src('A')] })).toBe('A · 2 reports')
  })
})
