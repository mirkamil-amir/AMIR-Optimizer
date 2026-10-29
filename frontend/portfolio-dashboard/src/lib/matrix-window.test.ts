import { describe, expect, it } from 'vitest'
import { MATRIX, cellAt, cellSize, visibleRange } from './matrix-window'

describe('matrix window', () => {
  it('fills the box for a few stocks, keeps a minimum for many, and never grows giant tiles', () => {
    expect(cellSize(10, 768)).toBe(Math.floor((768 - 72 - 8) / 10) - 2)
    expect(cellSize(228, 768)).toBe(MATRIX.minCell)
    expect(cellSize(2, 1400)).toBe(MATRIX.maxCell)
    expect(cellSize(10, 0)).toBe(cellSize(10, MATRIX.fallback.width))
  })

  it('renders only the rows/columns in view plus overscan, clamped to the matrix', () => {
    const step = 36 + 2
    // 228 stocks, scrolled 3,800 px (100 cells) into a 678 px wide box.
    const r = visibleRange(228, 3800, 678, 72, step)
    expect(r.from).toBe(100 - MATRIX.overscan)
    expect(r.to).toBe(Math.ceil((3800 + 678 - 72) / step) + MATRIX.overscan)
    expect(r.to - r.from + 1).toBeLessThan(30)
    expect(visibleRange(228, 1e6, 678, 72, step)).toEqual({ from: 227, to: 227 })
    expect(visibleRange(3, 0, 678, 72, step)).toEqual({ from: 0, to: 2 })
    expect(visibleRange(0, 0, 678, 72, step)).toEqual({ from: 0, to: -1 })
    // Unmeasured box: a typical window, not all 228 rows.
    expect(visibleRange(228, 0, 0, 72, step).to).toBeLessThan(40)
  })

  it('finds the cell under the pointer and nothing in the gaps', () => {
    expect(cellAt(0, 0, 3, 36)).toEqual({ i: 0, j: 0 })
    expect(cellAt(38 * 2 + 5, 38 + 1, 3, 36)).toEqual({ i: 1, j: 2 })
    expect(cellAt(37, 5, 3, 36)).toBeNull()
    expect(cellAt(38 * 3 + 1, 0, 3, 36)).toBeNull()
  })
})
