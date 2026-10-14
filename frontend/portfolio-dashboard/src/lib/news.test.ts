import { describe, expect, it } from 'vitest'
import { publishedLabel, safeExternalUrl, sourceLine } from './news'
import { newsParams } from './queries'

describe('news helpers', () => {
  const now = new Date('2026-10-15T12:00:00Z')

  it('labels recent publication times relatively and older ones by date', () => {
    expect(publishedLabel('2026-10-15T11:30:00Z', now)).toBe('30 min ago')
    expect(publishedLabel('2026-10-15T07:00:00Z', now)).toBe('5 h ago')
    expect(publishedLabel('2026-10-10T07:00:00Z', now)).toBe('Oct 10, 2026')
  })

  it('names corroborating publishers', () => {
    expect(sourceLine({ publisher: 'A', alsoReportedBy: [] })).toBe('A')
    expect(sourceLine({ publisher: 'A', alsoReportedBy: ['B'] })).toBe('A · also reported by B')
    expect(sourceLine({ publisher: 'A', alsoReportedBy: ['B', 'C'] })).toBe('A · also reported by 2 other publishers')
  })

  it('only renders http(s) links', () => {
    expect(safeExternalUrl('https://example.com/a')).toBe('https://example.com/a')
    expect(safeExternalUrl('javascript:alert(1)')).toBeNull()
    expect(safeExternalUrl('not a url')).toBeNull()
  })

  it('omits empty filters from the feed query', () => {
    expect(newsParams({ category: 'Earnings', watchlist: false, search: '', page: 2 })).toBe('category=Earnings&page=2')
    expect(newsParams({ watchlist: true, symbol: 'SAP.DE' })).toBe('watchlist=true&symbol=SAP.DE')
  })
})
