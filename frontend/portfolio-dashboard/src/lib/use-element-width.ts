import { useEffect, useState } from 'react'

/** Width changes smaller than this are ignored (a page scrollbar is about 15 px). */
const SETTLE_PX = 24

/**
 * Width of an element in CSS pixels, kept in step with resizes (a generous default before the first measurement).
 * Changes under 24 px are ignored: a page scrollbar that appears because the column set changed (about 15 px) must not
 * change the column set back – that would flip forever.
 */
export function useElementWidth(): [(el: HTMLDivElement | null) => void, number, HTMLDivElement | null] {
  const [el, setEl] = useState<HTMLDivElement | null>(null)
  const [width, setWidth] = useState(1200)
  useEffect(() => {
    if (!el || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(([entry]) => {
      const next = Math.round(entry.contentRect.width)
      setWidth((prev) => (prev === 1200 || Math.abs(next - prev) >= SETTLE_PX ? next : prev))
    })
    observer.observe(el)
    return () => observer.disconnect()
  }, [el])
  return [setEl, width, el]
}
