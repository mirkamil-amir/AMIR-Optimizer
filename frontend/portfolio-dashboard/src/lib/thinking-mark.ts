import { easeInOut, type CubeMove, type CubePose } from '@/lib/cube'

/**
 * The thinking logo (owner's choice, 2026-11-14, D-62): the mark is the flat 3 × 3 grid at rest; when the app starts
 * thinking it opens into the round ball of 27 balls and tumbles over two axes while its layers keep twisting; when the app
 * stops it finishes the twist in progress, undoes its twists quickly, lets the tumble come to rest on a quarter turn (where
 * the balls line up into the grid again) and closes back to the flat grid. Pure state machine: `step(dt, thinking)`
 * advances it by `dt` ms and returns the pose to draw (lib/cube.ts) – no clock, no DOM. Drawn by `lib/cube-animator.ts`.
 */

const QUARTER = Math.PI / 2
/** Opening into the ball and closing back to the grid. */
export const OPEN_MS = 420
/** One twist while thinking … */
export const BEAT_MS = 420
/** … and while undoing the twists on the way back. */
export const UNDO_MS = 110
/** Tumbling speed about the vertical and the left–right axis (radians per ms). */
const YAW_SPEED = (2 * Math.PI) / 3600
const PITCH_SPEED = (2 * Math.PI) / 5400
/** The tumble settles onto the next quarter turn in at least this long. */
const SETTLE_MS = 650

/** R U R′ U′ – six times over it returns exactly to solved. */
const CHAIN: readonly CubeMove[] = [{ axis: 0, turn: 1 }, { axis: 1, turn: 1 }, { axis: 0, turn: -1 }, { axis: 1, turn: -1 }]
const CHAIN_PERIOD = 24

export type ThinkingPhase = 'rest' | 'opening' | 'thinking' | 'settling' | 'closing'

const inverse = (m: CubeMove): CubeMove => ({ axis: m.axis, turn: (-m.turn) as 1 | -1 })

export class ThinkingMark {
  phase: ThinkingPhase = 'rest'
  private depth = 0
  private yaw = 0
  private pitch = 0
  /** Twists made since solved, and how many of the chain that is. */
  private done: CubeMove[] = []
  private current: { move: CubeMove; progress: number; ms: number; undo: boolean } | null = null
  private settle: { from: [number, number]; to: [number, number]; t: number; ms: number } | null = null
  /** On the way back: undo the twists made, or finish the chain forward when that is shorter. */
  private forward = false

  /** True while the mark is anything but the still grid. */
  get moving() { return this.phase !== 'rest' }

  /** Advances by `dt` ms; `thinking` is whether the app is working right now. */
  step(dt: number, thinking: boolean): CubePose {
    switch (this.phase) {
      case 'rest':
        if (thinking) this.phase = 'opening'
        break
      case 'opening':
        this.tumble(dt)
        this.twist(dt)
        this.depth = Math.min(1, this.depth + dt / OPEN_MS)
        if (!thinking) this.startSettling()
        else if (this.depth >= 1) this.phase = 'thinking'
        break
      case 'thinking':
        this.tumble(dt)
        this.twist(dt)
        if (!thinking) this.startSettling()
        break
      case 'settling':
        this.unwind(dt)
        if (this.settle) {
          this.settle.t += dt
          const k = easeInOut(this.settle.t / this.settle.ms)
          this.yaw = this.settle.from[0] + (this.settle.to[0] - this.settle.from[0]) * k
          this.pitch = this.settle.from[1] + (this.settle.to[1] - this.settle.from[1]) * k
        }
        if (!this.current && !this.done.length && this.settle && this.settle.t >= this.settle.ms) {
          this.yaw = 0; this.pitch = 0; this.settle = null // a whole number of quarter turns: the same grid
          this.phase = 'closing'
        }
        break
      case 'closing':
        this.depth = Math.max(0, this.depth - dt / OPEN_MS)
        if (thinking) this.phase = 'opening'
        else if (this.depth <= 0) this.phase = 'rest'
        break
    }
    return this.pose()
  }

  pose(): CubePose {
    return {
      depth: easeInOut(this.depth), yaw: this.yaw, pitch: this.pitch, done: [...this.done],
      current: this.current ? { move: this.current.move, progress: this.current.progress } : undefined,
    }
  }

  private tumble(dt: number) {
    this.yaw = (this.yaw + dt * YAW_SPEED) % (2 * Math.PI)
    this.pitch = (this.pitch + dt * PITCH_SPEED) % (2 * Math.PI)
  }

  /** The chain of twists while thinking: one per beat, without pauses. */
  private twist(dt: number) {
    let left = dt
    while (left > 0) {
      if (!this.current) this.current = { move: CHAIN[this.done.length % CHAIN.length], progress: 0, ms: BEAT_MS, undo: false }
      const need = (1 - this.current.progress) * this.current.ms
      if (left < need) { this.current.progress += left / this.current.ms; return }
      left -= need
      this.done.push(this.current.move)
      this.current = null
      if (this.done.length === CHAIN_PERIOD) this.done = [] // six rounds of R U R′ U′: solved again
    }
  }

  private startSettling() {
    this.phase = 'settling'
    if (this.current) this.current.ms = Math.min(this.current.ms, UNDO_MS / Math.max(0.05, 1 - this.current.progress))
    const next = (a: number) => Math.ceil(a / QUARTER - 1e-9) * QUARTER
    const made = this.done.length + (this.current ? 1 : 0)
    this.forward = CHAIN_PERIOD - made < made
    const twists = Math.min(made, CHAIN_PERIOD - made) + (this.current ? 1 : 0)
    this.settle = { from: [this.yaw, this.pitch], to: [next(this.yaw), next(this.pitch)], t: 0, ms: Math.max(SETTLE_MS, twists * UNDO_MS) }
  }

  /**
   * On the way back, quickly: finish the twist in progress, then either undo the twists made (last first) or play the
   * rest of the chain forward – whichever is shorter (at most 12 twists).
   */
  private unwind(dt: number) {
    let left = dt
    while (left > 0) {
      if (!this.current) {
        if (!this.done.length) return
        if (this.forward) this.current = { move: CHAIN[this.done.length % CHAIN.length], progress: 0, ms: UNDO_MS, undo: false }
        else this.current = { move: inverse(this.done.pop()!), progress: 0, ms: UNDO_MS, undo: true }
      }
      const need = (1 - this.current.progress) * this.current.ms
      if (left < need) { this.current.progress += left / this.current.ms; return }
      left -= need
      if (!this.current.undo) this.done.push(this.current.move) // a forward twist counts; an undone one is gone
      this.current = null
      if (this.done.length === CHAIN_PERIOD) this.done = [] // the chain completed: solved
    }
  }
}
