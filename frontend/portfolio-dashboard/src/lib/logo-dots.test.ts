import { describe, expect, it } from 'vitest'
import favicon from '../../public/favicon.svg?raw'
import { LOGO_DOT_RADIUS, LOGO_GRID, logoDots } from './logo-dots'

describe('logoDots', () => {
  const dots = logoDots()

  it('is a 3 × 3 grid of equal dots inside the box', () => {
    expect(LOGO_GRID).toBe(3)
    expect(dots).toHaveLength(9)
    expect(dots.map((d) => [d.x, d.y])).toEqual([[20, 20], [50, 20], [80, 20], [20, 50], [50, 50], [80, 50], [20, 80], [50, 80], [80, 80]])
    for (const d of dots) {
      expect(d.x - LOGO_DOT_RADIUS).toBeGreaterThan(0)
      expect(d.x + LOGO_DOT_RADIUS).toBeLessThan(100)
    }
  })

  it('orders the dots along the diagonal for the dot wave', () => {
    expect(dots.map((d) => d.step)).toEqual([0, 1, 2, 1, 2, 3, 2, 3, 4])
  })

  it('matches the favicon', () => {
    expect(favicon.match(/<circle /g)).toHaveLength(dots.length)
    for (const d of dots) expect(favicon).toContain(`cx="${d.x}" cy="${d.y}" r="${LOGO_DOT_RADIUS}"`)
  })
})
