/**
 * Geometry of the Matrix mark (owner's choice, 2026-11-14, D-62): 27 equal balls in one colour, arranged as a
 * 3 × 3 × 3 cube. Flat (`depth` 0) it looks straight at its front face, so the balls behind hide exactly behind the front
 * nine: the 3 × 3 grid of dots that is the logo. Open (`depth` 1) it is round like the start-up globe – the 26 outer balls
 * lie on a sphere, the centre ball is gone, the far side is fainter and nearer balls larger – seen turned 25° and tilted
 * 28°. Layers twist like a puzzle cube's (`done`, `current`), the whole ball turns (`yaw`, `pitch`); a twist keeps every
 * ball on the sphere. Pure: positions depend only on the pose. Motion: `lib/thinking-mark.ts`.
 *
 * Axes: x right, y up, z towards the viewer. Only the outer layers on the positive side turn (R = x, U = y, F = z).
 */

export type Vec3 = [number, number, number]
export type Axis = 0 | 1 | 2
/** A quarter twist of the outer layer on the positive side of `axis`, `turn` +1 or −1 (right-hand rule). */
export interface CubeMove { axis: Axis; turn: 1 | -1 }

/** What the mark looks like at one moment. */
export interface CubePose {
  /** Twists already made, in order. */
  done: readonly CubeMove[]
  /** The twist in progress and how far it is (0 … 1, eased here). */
  current?: { move: CubeMove; progress: number }
  /** Rotation of the whole ball about the vertical axis, radians. */
  yaw: number
  /** Rotation of the whole ball about the left–right axis, radians. */
  pitch: number
  /** 0 = the flat 3 × 3 grid, 1 = the round ball in 3D. */
  depth: number
}

/** One ball in drawing order (back to front): centre, radius and opacity in the 100 × 100 viewBox. */
export interface CubeBall { x: number; y: number; r: number; o: number }

/** Number of balls. */
export const CUBE_BALLS = 27
/** Flat: the grid's spacing and the dots' radius (dots at 20, 50, 80 with radius 11). */
export const FLAT_SPACING = 30
export const FLAT_RADIUS = 11
/** Open: spacing and radius at the centre depth – smaller, so the whole ball fits and the balls stand apart. */
const DEEP_SPACING = 22
const DEEP_RADIUS = 4.85
/** How much larger a ball gets per unit nearer the viewer. */
const DEPTH_GROWTH = 0.16
/** Open, the outer balls lie on a sphere of this radius (cube units). */
export const SPHERE_RADIUS = 1.45
/** Opacity of the farthest balls when open (the nearest are 1). */
const FAR_OPACITY = 0.35
/** The open view: turned and tilted this far. */
const VIEW_TURN = (25 * Math.PI) / 180
const VIEW_TILT = (28 * Math.PI) / 180

/** Rotates `v` by the angle with cosine `c` and sine `s` about `axis` (right-hand rule). */
function rotate(v: Vec3, axis: Axis, c: number, s: number): Vec3 {
  const [x, y, z] = v
  if (axis === 0) return [x, y * c - z * s, y * s + z * c]
  if (axis === 1) return [x * c + z * s, y, -x * s + z * c]
  return [x * c - y * s, x * s + y * c, z]
}
const turnBy = (v: Vec3, axis: Axis, angle: number) => rotate(v, axis, Math.cos(angle), Math.sin(angle))

const SOLVED: Vec3[] = []
for (let i = -1; i <= 1; i++) for (let j = -1; j <= 1; j++) for (let k = -1; k <= 1; k++) SOLVED.push([i, j, k])

const inLayer = (p: Vec3, axis: Axis) => Math.round(p[axis]) === 1

/** Ball centres after whole quarter twists, applied exactly (cosine and sine 0 or ±1, so nothing drifts). */
export function applyMoves(moves: readonly CubeMove[]): Vec3[] {
  let state = SOLVED
  for (const m of moves) state = state.map((p) => (inLayer(p, m.axis) ? rotate(p, m.axis, 0, m.turn) : p))
  return state
}

const clamp01 = (v: number) => Math.max(0, Math.min(1, v))
export const easeInOut = (v: number) => { const p = clamp01(v); return p < 0.5 ? 4 * p ** 3 : 1 - (-2 * p + 2) ** 3 / 2 }
const lerp = (a: number, b: number, k: number) => a + (b - a) * k

/** The balls of `pose`, back to front. */
export function cubeBalls(pose: CubePose): CubeBall[] {
  let state = applyMoves(pose.done)
  if (pose.current) {
    const { move, progress } = pose.current
    const angle = (move.turn * Math.PI / 2) * easeInOut(progress)
    state = state.map((p) => (inLayer(p, move.axis) ? turnBy(p, move.axis, angle) : p))
  }
  const k = clamp01(pose.depth)
  const spacing = lerp(FLAT_SPACING, DEEP_SPACING, k)
  const radius = lerp(FLAT_RADIUS / (1 + DEPTH_GROWTH), DEEP_RADIUS, k)
  return state
    .map((cube) => {
      // Round: every outer ball moves out (or in) to the sphere as the mark opens; the centre ball shrinks away.
      const len = Math.hypot(...cube)
      const p = (len ? cube.map((v) => v * lerp(1, SPHERE_RADIUS / len, k)) : cube) as Vec3
      const [x, y, z] = turnBy(turnBy(turnBy(turnBy(p, 0, pose.pitch), 1, pose.yaw), 1, -VIEW_TURN * k), 0, VIEW_TILT * k)
      const far = clamp01((SPHERE_RADIUS - z) / (2 * SPHERE_RADIUS))
      return {
        z, x: +(50 + spacing * x).toFixed(2), y: +(50 - spacing * y).toFixed(2),
        r: +(radius * (1 + DEPTH_GROWTH * z) * (len ? 1 : 1 - k)).toFixed(2),
        o: +lerp(1, 1 - (1 - FAR_OPACITY) * far, k).toFixed(3),
      }
    })
    .sort((a, b) => a.z - b.z)
    .map(({ x, y, r, o }) => ({ x, y, r, o }))
}

/** The logo at rest: the flat 3 × 3 grid. */
export const GRID_POSE: CubePose = { done: [], yaw: 0, pitch: 0, depth: 0 }
/** A thinking indicator with reduced motion: the round ball, standing still. */
export const STILL_BALL_POSE: CubePose = { done: [], yaw: 0, pitch: 0, depth: 1 }
