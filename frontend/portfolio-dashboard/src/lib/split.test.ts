import { afterEach, describe, expect, it } from 'vitest'
import { clampSplit, DEFAULT_SPLIT, loadSplit, MAX_SPLIT, MIN_SPLIT, saveSplit, splitAfterKey, splitAt } from './split'

describe('card split', () => {
  afterEach(() => window.localStorage.clear())

  it('follows the pointer within the row and stays between the limits', () => {
    expect(splitAt(600, 100, 1000)).toBeCloseTo(0.5)
    expect(splitAt(400, 100, 1000)).toBeCloseTo(0.3)
    expect(splitAt(0, 100, 1000)).toBe(MIN_SPLIT)
    expect(splitAt(5000, 100, 1000)).toBe(MAX_SPLIT)
    expect(splitAt(600, 100, 0)).toBe(DEFAULT_SPLIT)
    expect(clampSplit(Number.NaN)).toBe(DEFAULT_SPLIT)
  })

  it('moves in steps with the arrow keys, jumps with Home/End and resets with Enter', () => {
    expect(splitAfterKey(0.5, 'ArrowLeft')).toBeCloseTo(0.45)
    expect(splitAfterKey(0.5, 'ArrowRight')).toBeCloseTo(0.55)
    expect(splitAfterKey(0.74, 'ArrowRight')).toBe(MAX_SPLIT)
    expect(splitAfterKey(0.6, 'Home')).toBe(MIN_SPLIT)
    expect(splitAfterKey(0.6, 'End')).toBe(MAX_SPLIT)
    expect(splitAfterKey(0.3, 'Enter')).toBe(DEFAULT_SPLIT)
    expect(splitAfterKey(0.3, 'a')).toBeNull()
  })

  it('is remembered in this browser, clamped when read back', () => {
    expect(loadSplit()).toBe(DEFAULT_SPLIT)
    saveSplit(0.62)
    expect(loadSplit()).toBeCloseTo(0.62)
    window.localStorage.setItem('matrix.stockDetail.split', '9')
    expect(loadSplit()).toBe(MAX_SPLIT)
    window.localStorage.setItem('matrix.stockDetail.split', 'abc')
    expect(loadSplit()).toBe(DEFAULT_SPLIT)
  })
})
