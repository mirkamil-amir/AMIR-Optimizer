import { LOGO_GRID, logoDots, logoStrokes, TWINKLE_PHASES, type LogoDot } from '@/lib/logo-dots'

/** Frames per second of the working animation (owner's choice: 20, 2026-11-01). */
export const LOGO_FPS = 20
/** One slow twinkle cycle per dot, in seconds. */
const TWINKLE_S = 20
/** Foam dots keep their on/off state this long. */
const FOAM_STEP_S = 2.5
/** Fade from the still mark into the animation, in seconds. */
const FADE_IN_S = 0.6
const MAX_TORNADOES = 4

interface Swell {
  at: number; speed: number; height: number; tilt: number
  bend1: number; k1: number; p1: number; bend2: number; k2: number; p2: number
  peel: number; peelSpeed: number
}
interface Drop { at: number; x: number; y: number; power: number }
interface Tornado { at: number; life: number; x: number; y: number; vx: number; vy: number; spin: number; arms: number; size: number }

export interface LogoFrame {
  /** Opacity 0.05–1 per dot, in `logoDots()` order. */
  opacity: Float32Array
  /** The dot shows foam this frame (drawn in the foreground colour even on a diagonal). */
  foam: Uint8Array
}

/**
 * The "working" animation of the Matrix mark (chosen by the owner from the logo canvas on 2026-11-01): rolling surf,
 * stones dropped into the water, up to four tornadoes and a slow twinkle, all random so the picture never repeats.
 *
 * - Surf: swells roll in from the left in sets of two or three. Each crest bends (two drifting sines), surges, swings its
 *   angle and peels – the curl runs along the crest and leaves foam behind.
 * - Stones: every 0.5–1.8 s a stone falls on a random point of the M and sends out a damped ring.
 * - Tornadoes: up to four whirls with two or three spinning spiral arms and a calm eye wander across the logo.
 * - Twinkle: every dot brightens and dims on its own fixed phase over 20 s.
 *
 * Pure apart from the injected `random`; `advance(t)` spawns and retires events, `frame(t)` computes the dots. Time `t`
 * is in seconds since the motion started.
 */
export class LogoMotion {
  readonly dots: LogoDot[]
  private readonly n: number
  private readonly scale: number
  private readonly strokes: [number, number, number, number][]
  private swells: Swell[] = []
  private drops: Drop[] = []
  private tornadoes: Tornado[] = []
  private nextSwell = 0
  private leftInSet = 0
  private nextDrop = 0.8
  private nextTornado = 0.3
  private readonly out: LogoFrame

  private readonly random: () => number

  constructor(random: () => number = Math.random, n = LOGO_GRID) {
    this.random = random
    this.n = n
    this.scale = (n - 1) / 10
    this.strokes = logoStrokes(n)
    this.dots = logoDots(n)
    this.out = { opacity: new Float32Array(n * n), foam: new Uint8Array(n * n) }
  }

  /** Active events (for tests and diagnostics). */
  get counts() { return { swells: this.swells.length, drops: this.drops.length, tornadoes: this.tornadoes.length } }

  advance(t: number) {
    const { random: r, scale, n } = this
    const c = (n - 1) / 2
    if (t >= this.nextSwell) {
      if (this.leftInSet <= 0) this.leftInSet = 2 + Math.floor(r() * 2)
      this.swells.push({
        at: t, speed: (3 + r() * 1.8) * scale, height: 0.75 + r() * 0.45, tilt: (r() - 0.5) * 0.5,
        bend1: (3 + r() * 2) * scale, k1: (0.45 + r() * 0.35) / scale, p1: r() * 6.3,
        bend2: (1.5 + r() * 1.2) * scale, k2: (0.8 + r() * 0.6) / scale, p2: r() * 6.3,
        peel: r() < 0.5 ? 1 : -1, peelSpeed: (5 + r() * 4) * scale,
      })
      this.leftInSet--
      this.nextSwell = t + (this.leftInSet > 0 ? 0.7 + r() * 0.4 : 1 + r() * 1.2)
    }
    if (t >= this.nextDrop) {
      const [x1, y1, x2, y2] = this.strokes[Math.floor(r() * 4)]
      const u = r()
      this.drops.push({ x: x1 + u * (x2 - x1) + (r() - 0.5), y: y1 + u * (y2 - y1) + (r() - 0.5), at: t, power: 0.7 + r() * 0.5 })
      this.nextDrop = t + 0.5 + r() * 1.3
    }
    this.swells = this.swells.filter((w) => (t - w.at) * w.speed < n + 8 * scale)
    this.drops = this.drops.filter((d) => t - d.at < 3.2)
    this.tornadoes = this.tornadoes.filter((tn) => t - tn.at <= tn.life)
    if (this.tornadoes.length < MAX_TORNADOES && t >= this.nextTornado) {
      const ang = r() * 2 * Math.PI
      this.tornadoes.push({
        at: t, life: 4.5 + r() * 2.5, x: c + Math.cos(ang) * c * 0.9, y: c + Math.sin(ang) * c * 0.9,
        vx: -Math.cos(ang + (r() - 0.5)) * (0.8 + r() * 0.6) * scale, vy: -Math.sin(ang + (r() - 0.5)) * (0.8 + r() * 0.6) * scale,
        spin: (r() < 0.5 ? 1 : -1) * (5 + r() * 3), arms: 2 + Math.floor(r() * 2), size: (2.2 + r() * 1.2) * scale,
      })
      this.nextTornado = t + 0.3 + r() * 0.8
    }
  }

  /** Dot opacities and foam at time `t` (the returned arrays are reused between calls). */
  frame(t: number): LogoFrame {
    const { n, scale, out } = this
    const c = (n - 1) / 2
    const fade = Math.min(1, t / FADE_IN_S)
    const twisters = this.tornadoes.map((tn) => {
      const age = t - tn.at
      return {
        ...tn, age,
        cx: tn.x + tn.vx * age + 0.8 * Math.sin(3.1 * age + tn.spin),
        cy: tn.y + tn.vy * age + 0.8 * Math.cos(2.3 * age + tn.spin),
        power: Math.sin(Math.PI * Math.min(1, age / tn.life)),
      }
    })
    for (const dot of this.dots) {
      const { row, col } = dot
      const glow = dot.opacity
      // Surf: each crest is a bending line rolling in from the left; the break point (peel) runs along it.
      let h = 0, foam = 0
      for (const w of this.swells) {
        const age = t - w.at
        const peelRow = (w.peel > 0 ? -2 * scale : n - 1 + 2 * scale) + w.peel * w.peelSpeed * age
        const fromPeel = (row - peelRow) * w.peel
        const broken = fromPeel < 0
        const curl = 5 * scale * Math.exp(-((fromPeel / (3 * scale)) ** 2))
        const crest = age * w.speed - 3 * scale + w.tilt * (row - c)
          + w.bend1 * Math.sin(w.k1 * row + 4.6 * age + w.p1) + w.bend2 * Math.sin(w.k2 * row - 6.5 * age + w.p2) + curl
          + 2 * scale * Math.sin(3.4 * age + w.p2) + 0.6 * (row - c) * Math.sin(1.8 * age + w.p1)
        const s = (col - crest) / scale
        const shape = s > 0 ? Math.exp(-((s / (broken ? 0.9 : 0.6)) ** 2)) : Math.exp(-((s / 2.8) ** 2))
        h += w.height * shape * (0.5 + 0.7 * Math.cos(2 * Math.PI * s - 15 * age))
        if (broken && s > -1.8 && s < 0.9) foam = Math.max(foam, w.height * (1 - Math.abs(s + 0.45) / 1.35))
        else if (!broken && curl > 0.3 * scale && s > -0.4 && s < 0.6) foam = Math.max(foam, 0.6 * w.height)
      }
      // Stones: a ring packet at radius speed·age, damped with age and distance.
      for (const drop of this.drops) {
        const age = t - drop.at
        const dist = Math.hypot(col - drop.x, row - drop.y)
        const ring = (dist - 3.2 * scale * age) / (2.4 * scale)
        h += 0.9 * drop.power * Math.exp(-age / 1.4) * Math.exp(-ring * ring) * Math.cos(2 * Math.PI * ring) / Math.sqrt(1 + dist / scale)
      }
      // Tornadoes: spiral arms around a dark calm eye, fading with distance.
      for (const tw of twisters) {
        const dist = Math.hypot(col - tw.cx, row - tw.cy) / tw.size
        const swirl = Math.cos(tw.arms * Math.atan2(row - tw.cy, col - tw.cx) - 2.6 * dist * tw.arms + tw.spin * tw.age)
        const body = Math.exp(-dist * dist * 0.9)
        h += tw.power * (2.4 * body * swirl - 2.2 * Math.exp(-dist * dist * 9))
        if (body > 0.3 && swirl > 0.6) foam = Math.max(foam, tw.power)
      }
      // Foam flickers per dot in steps of FOAM_STEP_S (a fixed pseudo-random draw per dot and step).
      const draw = Math.abs(Math.sin(row * 12.9898 + col * 78.233 + Math.floor(t / FOAM_STEP_S) * 37.719) * 43758.5453) % 1
      const sparkle = foam > 0.2 && draw < 0.5 ? foam : 0
      const twinkle = 0.45 + 0.55 * (0.5 + 0.5 * Math.cos(2 * Math.PI * (t / TWINKLE_S - dot.phase / TWINKLE_PHASES)))
      const moving = Math.max(0.05, Math.min(1, (glow * 0.65 + 0.75 * (0.4 + 0.6 * glow) * h) * twinkle + 0.5 * sparkle))
      const i = row * n + col
      out.opacity[i] = glow + (moving - glow) * fade
      out.foam[i] = fade === 1 && sparkle > 0.5 ? 1 : 0
    }
    return out
  }
}
