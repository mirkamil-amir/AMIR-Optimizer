import { describe, expect, it } from 'vitest'
import favicon from '../../public/favicon.svg?raw'
import { applyMoves, CUBE_BALLS, cubeBalls, FLAT_RADIUS, GRID_POSE, SPHERE_RADIUS, STILL_BALL_POSE, type CubeMove } from './cube'

/** The distinct places of the balls, frontmost per place. */
const places = (balls: { x: number; y: number; r: number }[]) => {
  const front = new Map<string, number>()
  for (const b of balls) front.set(`${b.x},${b.y}`, b.r)
  return front
}

describe('Matrix mark geometry', () => {
  it('at rest is the flat 3 × 3 grid: 27 balls, the front nine at 20/50/80 with radius 11, the rest hidden behind them', () => {
    const balls = cubeBalls(GRID_POSE)
    expect(balls).toHaveLength(CUBE_BALLS)
    const front = places(balls)
    expect([...front.keys()].sort()).toEqual(['20,20', '20,50', '20,80', '50,20', '50,50', '50,80', '80,20', '80,50', '80,80'])
    for (const r of front.values()) expect(r).toBeCloseTo(FLAT_RADIUS, 5)
    for (const b of balls) expect(b.o).toBe(1)
  })

  it('matches the favicon: the nine dots, no frame, navy on light tab bars and white on dark ones', () => {
    expect(favicon.match(/<circle /g)).toHaveLength(9)
    for (const [place, r] of places(cubeBalls(GRID_POSE))) {
      const [x, y] = place.split(',')
      expect(favicon).toContain(`cx="${x}" cy="${y}" r="${r}"`)
    }
    expect(favicon).not.toContain('<rect')
    expect(favicon).toMatch(/prefers-color-scheme: dark\) \{ circle \{ fill: #ffffff/)
  })

  it('opens into a round ball inside the box: the far side fainter, the centre ball gone', () => {
    const balls = cubeBalls(STILL_BALL_POSE)
    for (const b of balls) {
      expect(b.x - b.r).toBeGreaterThanOrEqual(0)
      expect(b.x + b.r).toBeLessThanOrEqual(100)
      expect(b.y - b.r).toBeGreaterThanOrEqual(0)
      expect(b.y + b.r).toBeLessThanOrEqual(100)
    }
    expect(balls.filter((b) => b.r === 0)).toHaveLength(1) // the centre ball
    const visible = balls.filter((b) => b.r > 0)
    expect(Math.min(...visible.map((b) => b.o))).toBeLessThan(0.6)
    expect(Math.max(...visible.map((b) => b.o))).toBeGreaterThan(0.9)
    // Drawn back to front: the faint far side first.
    expect(visible[0].o).toBeLessThan(visible[visible.length - 1].o)
  })

  it('twists keep every outer ball on the sphere, and a twist and its reverse undo each other', () => {
    const r: CubeMove = { axis: 0, turn: 1 }
    const u: CubeMove = { axis: 1, turn: -1 }
    const halfway = { ...STILL_BALL_POSE, done: [r], current: { move: u, progress: 0.5 } }
    const radii = cubeBalls(halfway).filter((b) => b.r > 0).map((b) => b.r)
    expect(radii).toHaveLength(26)
    expect(applyMoves([r, u, { axis: 1, turn: 1 }, { axis: 0, turn: -1 }])).toEqual(applyMoves([]))
    expect(SPHERE_RADIUS).toBeGreaterThan(1)
  })

  it('a quarter turn of the whole cube lines up into the same grid', () => {
    const turned = cubeBalls({ ...GRID_POSE, yaw: Math.PI / 2, pitch: -Math.PI })
    expect([...places(turned).keys()].sort()).toEqual([...places(cubeBalls(GRID_POSE)).keys()].sort())
  })
})
