/**
 * Which columns of a wide table fit the space the table really has (not the screen): columns are added in priority
 * order while the sum of their minimum widths fits; the first one that does not fit stops the list, so a lower
 * priority never appears while a higher one is hidden. Priority 1 columns are always shown (phones scroll sideways).
 */
export interface FitColumn {
  key: string
  /** 1 = always shown; higher numbers are dropped first. */
  priority: number
  /** Smallest useful width in CSS pixels, padding included. */
  minWidth: number
}

export function fitColumns(columns: FitColumn[], available: number): { visible: Set<string>; hidden: string[] } {
  const ordered = columns.map((c, i) => ({ c, i })).sort((a, b) => a.c.priority - b.c.priority || a.i - b.i).map((x) => x.c)
  const visible = new Set<string>()
  let used = 0
  let stopped = false
  for (const c of ordered) {
    if (c.priority <= 1) { visible.add(c.key); used += c.minWidth; continue }
    if (!stopped && used + c.minWidth <= available) { visible.add(c.key); used += c.minWidth } else stopped = true
  }
  return { visible, hidden: columns.filter((c) => !visible.has(c.key)).map((c) => c.key) }
}
