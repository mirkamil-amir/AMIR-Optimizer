import { logoDots, type LogoDot } from '@/lib/logo-dots'

const SPREAD_MS = 650 // the dots set off one by one over this span
const FLY_MS = 480 // a dot's flight takes this long, plus up to …
const FLY_SPREAD_MS = 260 // … this much more
const EDGE = 12 // dots start just outside the 100 × 100 box

export interface LogoFrame {
  /** Centre and radius factor (0 = hidden, 1 = its still size) per dot, in `logoDots()` order. */
  x: Float32Array
  y: Float32Array
  scale: Float32Array
}

interface Flight { dot: number; axis: 'h' | 'v'; from: number; delay: number; fly: number }

/** A fixed pseudo-random number per dot and purpose, so every arrival looks alike. */
function rnd(dot: LogoDot, k: number) {
  const s = Math.sin(dot.col * 127.1 + dot.row * 311.7 + k * 74.7) * 43758.5453
  return s - Math.floor(s)
}
const clamp = (v: number) => Math.max(0, Math.min(1, v))
const easeOut = (v: number) => 1 - (1 - clamp(v)) ** 3
/** Lands slightly too large and settles – the small pop of an arriving dot. */
const settle = (v: number) => { const u = clamp(v) - 1; return 1 + 2.9 * u ** 3 + 1.9 * u ** 2 }

/**
 * The one-time arrival of the Matrix mark (owner's choice, 2026-11-11, D-58): wherever the mark appears, each of its nine
 * dots flies in once from just outside the box – on its own axis (straight across or straight up/down, never diagonally),
 * side, start and speed – and lands with a small pop. Then the mark stays still. Pure: `introFrame(t)` depends only on `t`
 * (milliseconds since the arrival started).
 */
export class LogoMotion {
  readonly dots = logoDots()
  /** Length of the arrival. */
  readonly introMs: number
  private readonly flights: Flight[]
  private readonly out: LogoFrame

  constructor() {
    this.flights = this.dots.map((dot, index) => {
      // Alternate the axis along the grid (with a little chance mixed in), so both directions always appear.
      const axis = ((dot.row + dot.col) % 2 === 0) !== (rnd(dot, 1) < 0.2) ? 'h' : 'v'
      const side = rnd(dot, 2) < 0.5 ? -1 : 1
      return {
        dot: index, axis, from: side < 0 ? -EDGE : 100 + EDGE,
        delay: rnd(dot, 3) * SPREAD_MS, fly: FLY_MS + rnd(dot, 4) * FLY_SPREAD_MS,
      }
    })
    this.introMs = Math.max(...this.flights.map((f) => f.delay + f.fly)) + 300
    const n = this.dots.length
    this.out = { x: new Float32Array(n), y: new Float32Array(n), scale: new Float32Array(n) }
  }

  /** The arrival at `t` ms; from `introMs` on it is the finished mark (the returned arrays are reused between calls). */
  introFrame(t: number): LogoFrame {
    const { out, dots } = this
    for (const f of this.flights) {
      const dot = dots[f.dot]
      const local = t - f.delay
      const p = easeOut(local / f.fly)
      out.x[f.dot] = f.axis === 'h' ? f.from + (dot.x - f.from) * p : dot.x
      out.y[f.dot] = f.axis === 'v' ? f.from + (dot.y - f.from) * p : dot.y
      out.scale[f.dot] = local <= 0 ? 0 : local < f.fly * 0.6 ? 0.7 : settle((local - f.fly * 0.6) / 300)
    }
    return out
  }
}
