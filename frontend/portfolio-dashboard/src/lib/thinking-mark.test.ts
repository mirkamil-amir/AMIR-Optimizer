import { describe, expect, it } from 'vitest'
import { cubeBalls, GRID_POSE } from './cube'
import { OPEN_MS, ThinkingMark } from './thinking-mark'

const grid = JSON.stringify(cubeBalls(GRID_POSE).map((b) => [b.x, b.y, b.r]).sort())
const shape = (m: ThinkingMark) => JSON.stringify(cubeBalls(m.pose()).map((b) => [b.x, b.y, b.r]).sort())

/** Steps the mark until it rests (or `limit` ms pass); returns the time it took. */
function settle(m: ThinkingMark, dt: number, limit = 10_000) {
  let t = 0
  while (m.moving && t < limit) { m.step(dt, false); t += dt }
  return t
}

describe('Thinking mark', () => {
  it('rests as the grid until the app thinks', () => {
    const m = new ThinkingMark()
    for (let i = 0; i < 20; i++) m.step(16, false)
    expect(m.moving).toBe(false)
    expect(shape(m)).toBe(grid)
  })

  it('opens into the ball and keeps tumbling and twisting while the app thinks', () => {
    const m = new ThinkingMark()
    let t = 0
    while (t < OPEN_MS + 50) { m.step(16, true); t += 16 }
    expect(m.phase).toBe('thinking')
    const a = shape(m)
    m.step(200, true)
    expect(shape(m)).not.toBe(a)
    expect(m.pose().depth).toBe(1)
  })

  it('returns exactly to the grid within two seconds whenever the app stops – even right after it started', () => {
    for (const thinkMs of [0, 16, 50, 150, 300, 420, 430, 777, 1000, 1500, 2100, 2500, 3000, 4000, 5040, 6000, 7777, 10080]) {
      for (const dt of [7, 16, 33]) {
        const m = new ThinkingMark()
        for (let t = 0; t < thinkMs; t += dt) m.step(dt, true)
        const took = settle(m, dt)
        expect(m.moving, `${thinkMs} ms, frames of ${dt} ms`).toBe(false)
        expect(shape(m), `${thinkMs} ms, frames of ${dt} ms`).toBe(grid)
        expect(took).toBeLessThanOrEqual(2000)
      }
    }
  })

  it('opens again when the app starts thinking while it closes', () => {
    const m = new ThinkingMark()
    for (let i = 0; i < 100; i++) m.step(16, true)
    for (let i = 0; i < 70; i++) m.step(16, false)
    for (let i = 0; i < 10; i++) m.step(16, true)
    expect(m.moving).toBe(true)
    settle(m, 16)
    expect(shape(m)).toBe(grid)
  })
})
