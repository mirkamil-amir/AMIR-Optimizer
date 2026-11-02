import { describe, expect, it } from 'vitest'
import favicon from '../../public/favicon.svg?raw'
import { LOGO_GRID, logoDots, TWINKLE_PHASES } from './logo-dots'

describe('logoDots', () => {
  const dots = logoDots()
  const at = (col: number, row: number) => dots[row * LOGO_GRID + col]

  it('draws an 11 × 11 matrix inside the tile', () => {
    expect(dots).toHaveLength(121)
    expect(Math.min(...dots.map((d) => d.x))).toBe(11)
    expect(Math.max(...dots.map((d) => d.y))).toBe(89)
  })

  it('lights the strokes of the M fully and fades the rest', () => {
    expect(at(1, 1).opacity).toBe(1) // top of the left leg
    expect(at(1, 9).opacity).toBe(1) // foot of the left leg
    expect(at(5, 5).opacity).toBe(1) // where the diagonals meet
    expect(at(9, 9).opacity).toBe(1) // foot of the right leg
    expect(at(5, 10).opacity).toBe(0.12) // below the centre, far from every stroke
  })

  it('makes the diagonals gold and the legs white', () => {
    expect(at(3, 3).gold).toBe(true)
    expect(at(7, 3).gold).toBe(true)
    expect(at(1, 6).gold).toBe(false)
    expect(at(9, 6).gold).toBe(false)
  })

  it('scatters the twinkle over every phase', () => {
    expect(new Set(dots.map((d) => d.phase)).size).toBe(TWINKLE_PHASES)
    expect(at(1, 1).phase).not.toBe(at(2, 1).phase)
  })

  it('matches the favicon', () => {
    expect(favicon.match(/<circle /g)).toHaveLength(dots.length)
    for (const d of dots) expect(favicon).toContain(`cx="${d.x}" cy="${d.y}"`)
  })
})
