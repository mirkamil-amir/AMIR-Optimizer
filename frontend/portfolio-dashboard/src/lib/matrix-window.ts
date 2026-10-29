/**
 * Geometry of a large square matrix drawn in a scroll box (the correlation heatmap): the cell size and which rows and
 * columns are in view, so only those cells are rendered. A matrix of 228 stocks has ~52,000 cells; rendering all of them
 * took ~6 s to open in full screen (2026-10-29), the visible window is a few hundred.
 */
export const MATRIX = {
  /** Width of the row labels on the left. */
  labelWidth: 72,
  /** Height of the column labels on top. */
  labelHeight: 20,
  gap: 2,
  minCell: 36,
  /** Small matrices grow to fill the box, but never into giant tiles. */
  maxCell: 96,
  /** Extra rows/columns rendered beyond each edge so fast scrolling shows no blank strip. */
  overscan: 3,
  /** Box size assumed before it has been measured (and in tests, where nothing has a layout). */
  fallback: { width: 1024, height: 768 },
} as const

/** Side of one cell (without the gap) for `n` stocks in a box `width` pixels wide. */
export function cellSize(n: number, width: number) {
  const w = width > 0 ? width : MATRIX.fallback.width
  const fit = Math.floor((w - MATRIX.labelWidth - 8) / Math.max(1, n)) - MATRIX.gap
  return Math.max(MATRIX.minCell, Math.min(MATRIX.maxCell, fit))
}

/** First and last index (inclusive) of the rows or columns visible along one axis, with overscan, clamped to the matrix. */
export function visibleRange(n: number, scroll: number, viewport: number, label: number, step: number) {
  if (n === 0) return { from: 0, to: -1 }
  // Not measured yet: assume a typical box, never "everything" (that is the slow path this module exists to avoid).
  const size = viewport > 0 ? viewport : Math.max(MATRIX.fallback.width, MATRIX.fallback.height)
  const from = Math.min(n - 1, Math.max(0, Math.floor(scroll / step) - MATRIX.overscan))
  const to = Math.min(n - 1, Math.ceil((scroll + size - label) / step) + MATRIX.overscan)
  return { from, to: Math.max(from, to) }
}

/** The cell under a point measured from the top-left corner of the cell area, or null in a gap or outside. */
export function cellAt(x: number, y: number, n: number, cell: number) {
  const step = cell + MATRIX.gap
  const i = Math.floor(y / step)
  const j = Math.floor(x / step)
  if (i < 0 || j < 0 || i >= n || j >= n || x - j * step > cell || y - i * step > cell) return null
  return { i, j }
}
