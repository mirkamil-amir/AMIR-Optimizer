/** A copy of `items` with the element at `from` moved to `to` (both clamped to the list); the rest keep their order. */
export function moveItem<T>(items: readonly T[], from: number, to: number): T[] {
  const n = items.length
  if (n === 0) return []
  const f = Math.max(0, Math.min(n - 1, from))
  const t = Math.max(0, Math.min(n - 1, to))
  const copy = items.slice()
  const [moved] = copy.splice(f, 1)
  copy.splice(t, 0, moved)
  return copy
}

/**
 * Where a dragged row lands: the index whose slot the dragged row's centre has entered, from the row midpoints measured
 * when the drag started (`mids[i]` = vertical centre of row i) and the dragged row's current centre.
 */
export function dropIndex(mids: readonly number[], from: number, center: number): number {
  let to = from
  for (let i = from + 1; i < mids.length && center > mids[i]; i++) to = i
  for (let i = from - 1; i >= 0 && center < mids[i]; i--) to = i
  return to
}

/** How far row `index` moves aside while row `from` is dragged to `to` (one row height, up or down; 0 if unaffected). */
export function shiftFor(index: number, from: number, to: number, height: number): number {
  if (from < to && index > from && index <= to) return -height
  if (from > to && index >= to && index < from) return height
  return 0
}
