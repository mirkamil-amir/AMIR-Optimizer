import { describe, expect, it } from 'vitest'
import { fitColumns } from './column-fit'

const cols = [
  { key: 'ticker', priority: 1, minWidth: 100 },
  { key: 'price', priority: 1, minWidth: 120 },
  { key: 'company', priority: 3, minWidth: 190 },
  { key: 'trend', priority: 2, minWidth: 130 },
  { key: 'sector', priority: 4, minWidth: 130 },
  { key: 'index', priority: 5, minWidth: 60 },
]

describe('fitColumns', () => {
  it('always keeps priority 1, even when the space is too small', () => {
    const { visible, hidden } = fitColumns(cols, 150)
    expect([...visible]).toEqual(['ticker', 'price'])
    expect(hidden).toEqual(['company', 'trend', 'sector', 'index'])
  })

  it('adds columns by priority while they fit and keeps the table order in the hidden list', () => {
    expect([...fitColumns(cols, 360).visible]).toEqual(['ticker', 'price', 'trend'])
    expect([...fitColumns(cols, 540).visible].sort()).toEqual(['company', 'price', 'ticker', 'trend'])
    expect(fitColumns(cols, 10_000).hidden).toEqual([])
  })

  it('never shows a lower priority while a higher one is hidden, even if it would fit', () => {
    // 100 + 120 + 130 = 350; company (190) does not fit in 420, so the narrow index (60) must not appear either.
    const { visible } = fitColumns(cols, 420)
    expect(visible.has('company')).toBe(false)
    expect(visible.has('index')).toBe(false)
  })
})
