export interface StockPosition {
  /** 0-based index in the list, or -1 when the stock is no longer in it (e.g. filters changed while the panel was open). */
  index: number
  total: number
  previous: string | null
  next: string | null
}

/**
 * Previous/next stock in the list exactly as displayed (current filters, sort and page). No wraparound: the first stock
 * has no previous and the last no next. A stock that left the list has neither, so navigation cannot jump unexpectedly.
 */
export function stockPosition(symbols: readonly string[], current: string | null): StockPosition {
  const index = current === null ? -1 : symbols.indexOf(current)
  return {
    index,
    total: symbols.length,
    previous: index > 0 ? symbols[index - 1] : null,
    next: index >= 0 && index < symbols.length - 1 ? symbols[index + 1] : null,
  }
}
