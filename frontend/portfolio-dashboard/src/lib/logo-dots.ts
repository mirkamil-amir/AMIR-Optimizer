/** Dots per side of the logo's matrix. */
export const LOGO_GRID = 11

/** Twinkle phases: every dot gets one of these fixed, scattered offsets in the twinkle cycle. */
export const TWINKLE_PHASES = 20

/** The M's four strokes in grid cells: left leg (up), first diagonal (down), second diagonal (up), right leg (down). */
export function logoStrokes(n = LOGO_GRID): [number, number, number, number][] {
  const a = 1, b = n - 2, c = (n - 1) / 2
  return [[a, b, a, a], [a, a, c, c], [c, c, b, a], [b, a, b, b]]
}

export interface LogoDot {
  row: number
  col: number
  /** Centre in the 100 × 100 viewBox of the mark. */
  x: number
  y: number
  /** Glow 0.12–1: strongest on the strokes of the M, fading with the distance from them. */
  opacity: number
  /** The glow belongs to one of the two diagonal strokes (drawn in the accent colour). */
  gold: boolean
  /** Fixed, scattered phase 0…TWINKLE_PHASES-1 of the twinkle. */
  phase: number
}

/**
 * The Matrix mark (variation 3c, chosen by the owner on 2026-11-01): an M drawn through an 11 × 11 dot matrix – legs on
 * the second and second-to-last column, diagonals meeting in the centre cell. Every dot glows by its distance to the
 * nearest stroke; dots near a diagonal glow gold, all others white.
 */
export function logoDots(n = LOGO_GRID): LogoDot[] {
  const strokes = logoStrokes(n)
  const step = 78 / (n - 1)
  const scale = (n - 1) / 10
  const dots: LogoDot[] = []
  for (let row = 0; row < n; row++) {
    for (let col = 0; col < n; col++) {
      let distance = Infinity, stroke = 0
      strokes.forEach(([x1, y1, x2, y2], i) => {
        const dx = x2 - x1, dy = y2 - y1, len = dx * dx + dy * dy
        const t = Math.max(0, Math.min(1, len ? ((col - x1) * dx + (row - y1) * dy) / len : 0))
        const d = Math.hypot(x1 + t * dx - col, y1 + t * dy - row)
        if (d < distance) { distance = d; stroke = i }
      })
      dots.push({
        row, col,
        x: round(11 + col * step),
        y: round(11 + row * step),
        opacity: round(Math.max(0.12, 1 - distance / (2.6 * scale))),
        gold: (stroke === 1 || stroke === 2) && distance < 2 * scale,
        phase: (row * 7 + col * 13 + row * col * 3) % TWINKLE_PHASES,
      })
    }
  }
  return dots
}

const round = (v: number) => Math.round(v * 100) / 100
