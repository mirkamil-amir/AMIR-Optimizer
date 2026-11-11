/**
 * The Matrix mark (owner's choice, 2026-11-11, D-58): a 3 × 3 grid of round dots – the thinking animation's dots at rest.
 * The dot wave (`MatrixLoader`) uses exactly this geometry, so the mark and the loader are one family: when the app
 * thinks, the same dots light up in a diagonal wave.
 */

/** Dots per side. */
export const LOGO_GRID = 3
/** Radius of every dot in the 100 × 100 viewBox. */
export const LOGO_DOT_RADIUS = 11

export interface LogoDot {
  row: number
  col: number
  /** Centre in the 100 × 100 viewBox. */
  x: number
  y: number
  /** Position along the diagonal, 0 (top left) … 4 (bottom right): the dot wave lights the dots in this order. */
  step: number
}

const DOTS: LogoDot[] = Array.from({ length: LOGO_GRID * LOGO_GRID }, (_, k) => {
  const row = Math.floor(k / LOGO_GRID), col = k % LOGO_GRID
  return { row, col, x: 20 + col * 30, y: 20 + row * 30, step: row + col }
})

/** The nine dots, row by row from the top left. */
export function logoDots(): LogoDot[] {
  return DOTS
}
