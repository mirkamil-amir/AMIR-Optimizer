/**
 * Width split of two side-by-side cards (stock-detail panel: price history | accounting). The left card's share of the
 * row, kept between MIN_SPLIT and MAX_SPLIT so neither card becomes unusable; remembered in this browser (a display
 * preference, never sent to the server).
 */
export const DEFAULT_SPLIT = 0.5
export const MIN_SPLIT = 0.25
export const MAX_SPLIT = 0.75
/** One arrow-key step of the handle. */
export const SPLIT_STEP = 0.05
const STORAGE_KEY = 'matrix.stockDetail.split'

export function clampSplit(value: number): number {
  if (!Number.isFinite(value)) return DEFAULT_SPLIT
  // Rounded so repeated steps stay exact (0.6, not 0.6000000000000001).
  return Math.round(Math.min(MAX_SPLIT, Math.max(MIN_SPLIT, value)) * 1000) / 1000
}

/** The split for a pointer at clientX over a row spanning [left, left + width]. */
export function splitAt(clientX: number, left: number, width: number): number {
  return width > 0 ? clampSplit((clientX - left) / width) : DEFAULT_SPLIT
}

/** The split after a key on the focused handle, or null for keys the handle does not use. */
export function splitAfterKey(value: number, key: string): number | null {
  switch (key) {
    case 'ArrowLeft': return clampSplit(value - SPLIT_STEP)
    case 'ArrowRight': return clampSplit(value + SPLIT_STEP)
    case 'Home': return MIN_SPLIT
    case 'End': return MAX_SPLIT
    case 'Enter': return DEFAULT_SPLIT
    default: return null
  }
}

export function loadSplit(): number {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY)
    return raw === null ? DEFAULT_SPLIT : clampSplit(Number(raw))
  } catch {
    return DEFAULT_SPLIT
  }
}

export function saveSplit(value: number): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, String(clampSplit(value)))
  } catch {
    // Storage unavailable (private mode): the split simply is not remembered.
  }
}
