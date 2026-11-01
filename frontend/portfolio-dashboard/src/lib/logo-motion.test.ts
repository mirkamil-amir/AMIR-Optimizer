import { describe, expect, it } from 'vitest'
import { LOGO_FPS, LogoMotion } from './logo-motion'

/** Deterministic random numbers (mulberry32) so every run sees the same weather. */
function seeded(seed: number) {
  return () => {
    seed = (seed + 0x6d2b79f5) | 0
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed)
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296
  }
}

/** Runs the motion frame by frame like the ticker and returns the frame at `seconds`. */
function runTo(motion: LogoMotion, seconds: number) {
  let frame = motion.frame(0)
  for (let i = 0; i <= seconds * LOGO_FPS; i++) {
    const t = i / LOGO_FPS
    motion.advance(t)
    frame = motion.frame(t)
  }
  return frame
}

describe('LogoMotion', () => {
  it('starts as the still mark and fades into the movement', () => {
    const motion = new LogoMotion(seeded(1))
    motion.advance(0)
    const first = motion.frame(0)
    motion.dots.forEach((dot, i) => expect(first.opacity[i]).toBeCloseTo(dot.opacity, 5))
    expect(first.foam.every((f) => f === 0)).toBe(true)
  })

  it('keeps every dot visible and never brighter than full', () => {
    const motion = new LogoMotion(seeded(2))
    for (const seconds of [1, 5, 12]) {
      const frame = runTo(motion, seconds)
      for (const o of frame.opacity) {
        expect(o).toBeGreaterThanOrEqual(0.05)
        expect(o).toBeLessThanOrEqual(1)
      }
    }
  })

  it('brings in surf, stones and at most four tornadoes', () => {
    const motion = new LogoMotion(seeded(3))
    let most = { swells: 0, drops: 0, tornadoes: 0 }
    for (let i = 0; i <= 30 * LOGO_FPS; i++) {
      motion.advance(i / LOGO_FPS)
      const c = motion.counts
      most = { swells: Math.max(most.swells, c.swells), drops: Math.max(most.drops, c.drops), tornadoes: Math.max(most.tornadoes, c.tornadoes) }
    }
    expect(most.swells).toBeGreaterThan(0)
    expect(most.drops).toBeGreaterThan(0)
    expect(most.tornadoes).toBe(4)
  })

  it('never repeats: two runs with different random numbers differ', () => {
    const a = Array.from(runTo(new LogoMotion(seeded(4)), 6).opacity)
    const b = Array.from(runTo(new LogoMotion(seeded(5)), 6).opacity)
    expect(a).not.toEqual(b)
  })

  it('is deterministic for the same random numbers', () => {
    const a = Array.from(runTo(new LogoMotion(seeded(6)), 4).opacity)
    const b = Array.from(runTo(new LogoMotion(seeded(6)), 4).opacity)
    expect(a).toEqual(b)
  })
})
