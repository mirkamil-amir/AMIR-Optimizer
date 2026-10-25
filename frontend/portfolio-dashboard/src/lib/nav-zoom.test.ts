import { describe, expect, it } from 'vitest'
import { ZOOM_RADIUS, zoomFor } from './nav-zoom'

describe('zoomFor', () => {
  it('is largest under the pointer and normal beyond the radius', () => {
    expect(zoomFor(0, 1.2, false)).toBeCloseTo(1.2)
    expect(zoomFor(ZOOM_RADIUS, 1.2, false)).toBe(1)
    expect(zoomFor(-ZOOM_RADIUS * 2, 1.2, false)).toBe(1)
  })
  it('shrinks with distance, the same above and below', () => {
    const near = zoomFor(10, 1.2, false)
    const far = zoomFor(40, 1.2, false)
    expect(near).toBeGreaterThan(far)
    expect(far).toBeGreaterThan(1)
    expect(zoomFor(-40, 1.2, false)).toBeCloseTo(far)
  })
  it('keeps the selected entry at the maximum', () => {
    expect(zoomFor(ZOOM_RADIUS * 3, 1.2, true)).toBe(1.2)
  })
})

describe('zoomFor along a row', () => {
  it('uses the wider radius it is given', () => {
    expect(zoomFor(100, 1.14, false, 130)).toBeGreaterThan(1)
    expect(zoomFor(100, 1.14, false)).toBe(1)
  })
})
