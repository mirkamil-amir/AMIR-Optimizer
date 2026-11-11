import { describe, expect, it } from 'vitest'
import { LogoMotion } from './logo-motion'

describe('LogoMotion (the one-time arrival)', () => {
  const motion = new LogoMotion()
  const dots = motion.dots
  const frames = (step = 1000 / 60) => {
    const out: number[] = []
    for (let t = 0; t < motion.introMs; t += step) out.push(t)
    return out
  }

  it('shows nothing before a dot starts, and the finished mark at the end', () => {
    const start = motion.introFrame(0)
    dots.forEach((_, i) => expect(start.scale[i]).toBe(0))
    const done = motion.introFrame(motion.introMs)
    dots.forEach((d, i) => {
      expect(done.x[i]).toBeCloseTo(d.x, 3)
      expect(done.y[i]).toBeCloseTo(d.y, 3)
      expect(done.scale[i]).toBeCloseTo(1, 3)
    })
    expect(motion.introMs).toBeLessThan(2000)
  })

  it('moves every dot along one axis only – straight across or straight up and down', () => {
    for (const t of frames()) {
      const f = motion.introFrame(t)
      dots.forEach((d, i) => expect(Math.abs(f.x[i] - d.x) > 1e-3 && Math.abs(f.y[i] - d.y) > 1e-3).toBe(false))
    }
  })

  it('flies the dots individually and in both directions', () => {
    const axes = dots.map((d, i) => {
      for (const t of frames(40)) {
        const f = motion.introFrame(t)
        if (Math.abs(f.x[i] - d.x) > 1e-3) return 'h'
        if (Math.abs(f.y[i] - d.y) > 1e-3) return 'v'
      }
      return '-'
    })
    expect(axes.filter((a) => a === 'h').length).toBeGreaterThanOrEqual(2)
    expect(axes.filter((a) => a === 'v').length).toBeGreaterThanOrEqual(2)
    expect(axes).not.toContain('-') // every dot flies
    // They set off one after another, not together.
    const starts = dots.map((_, i) => frames(10).find((t) => motion.introFrame(t).scale[i] > 0) ?? 0)
    expect(new Set(starts).size).toBeGreaterThanOrEqual(6)
  })

  it('is the same every time', () => {
    expect(Array.from(new LogoMotion().introFrame(1200).x)).toEqual(Array.from(new LogoMotion().introFrame(1200).x))
  })
})
