import { describe, expect, it } from 'vitest'
import { chartHeightFor } from './use-chart-height'

describe('chartHeightFor', () => {
  it('is 42 % of the viewport between 240 and 520 px', () => {
    expect(chartHeightFor(900)).toBe(378)
    expect(chartHeightFor(400)).toBe(240)
    expect(chartHeightFor(2000)).toBe(520)
  })
})
