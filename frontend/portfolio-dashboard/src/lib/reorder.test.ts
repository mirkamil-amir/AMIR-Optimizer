import { describe, expect, it } from 'vitest'
import { dropIndex, moveItem, shiftFor } from './reorder'

describe('moveItem', () => {
  it('moves down and up and keeps the others in order', () => {
    expect(moveItem(['a', 'b', 'c', 'd'], 0, 2)).toEqual(['b', 'c', 'a', 'd'])
    expect(moveItem(['a', 'b', 'c', 'd'], 3, 1)).toEqual(['a', 'd', 'b', 'c'])
    expect(moveItem(['a', 'b'], 1, 1)).toEqual(['a', 'b'])
  })

  it('clamps out-of-range positions and never changes the input', () => {
    const list = ['a', 'b', 'c']
    expect(moveItem(list, 0, 99)).toEqual(['b', 'c', 'a'])
    expect(moveItem(list, 2, -5)).toEqual(['c', 'a', 'b'])
    expect(list).toEqual(['a', 'b', 'c'])
    expect(moveItem([], 0, 1)).toEqual([])
  })
})

describe('dropIndex and shiftFor', () => {
  const mids = [20, 60, 100, 140] // four rows of 40 px

  it('lands where the dragged centre has passed the neighbours’ midpoints', () => {
    expect(dropIndex(mids, 0, 30)).toBe(0)
    expect(dropIndex(mids, 0, 61)).toBe(1)
    expect(dropIndex(mids, 0, 150)).toBe(3)
    expect(dropIndex(mids, 3, 99)).toBe(2)
    expect(dropIndex(mids, 3, 0)).toBe(0)
  })

  it('moves only the rows between the old and the new place, by one row height', () => {
    expect([0, 1, 2, 3].map((i) => shiftFor(i, 0, 2, 40))).toEqual([0, -40, -40, 0])
    expect([0, 1, 2, 3].map((i) => shiftFor(i, 3, 1, 40))).toEqual([0, 40, 40, 0])
    expect([0, 1, 2, 3].map((i) => shiftFor(i, 1, 1, 40))).toEqual([0, 0, 0, 0])
  })
})
