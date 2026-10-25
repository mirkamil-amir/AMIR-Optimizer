/**
 * Dock-like magnification of navigation entries (sidebar: vertical, top bar: horizontal): the closer the pointer is to an
 * entry along the list's axis, the larger its text, up to `--nav-zoom-max`; the selected entry (`data-zoom-active`) never
 * shrinks below that. The scale is written as a CSS variable on each entry (like the map camera, never React state), so
 * moving the pointer re-renders nothing.
 */

/** Pointer distance (px) at which an entry is back at normal size (sidebar). */
export const ZOOM_RADIUS = 72

/** Scale of an entry `distance` px from the pointer; `active` entries keep the maximum. Pure – unit-tested. */
export function zoomFor(distance: number, max: number, active: boolean, radius = ZOOM_RADIUS): number {
  const near = Math.max(0, 1 - Math.abs(distance) / radius)
  // Smooth falloff (ease-out): neighbours grow a little, the entry under the pointer the most.
  const scale = 1 + (max - 1) * near * near * (3 - 2 * near)
  return active ? Math.max(scale, max) : scale
}

/**
 * Applies the magnification to every `.nav-zoom` entry inside `area` while the pointer moves over it: `axis` 'y' for a
 * vertical list, 'x' for a row (wider entries, so a wider radius). Returns a cleanup.
 */
export function attachNavZoom(area: HTMLElement, axis: 'x' | 'y' = 'y'): () => void {
  const radius = axis === 'x' ? 130 : ZOOM_RADIUS
  if (typeof window !== 'undefined' && window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return () => {}
  const entries = () => Array.from(area.querySelectorAll<HTMLElement>('.nav-zoom'))
  const max = () => Number.parseFloat(getComputedStyle(area).getPropertyValue('--nav-zoom-max')) || 1.2
  let frame = 0
  const move = (e: PointerEvent) => {
    if (e.pointerType === 'touch') return
    cancelAnimationFrame(frame)
    frame = requestAnimationFrame(() => {
      const m = max()
      for (const el of entries()) {
        const r = el.getBoundingClientRect()
        const distance = axis === 'x' ? e.clientX - (r.left + r.width / 2) : e.clientY - (r.top + r.height / 2)
        el.style.setProperty('--nav-zoom', zoomFor(distance, m, el.hasAttribute('data-zoom-active'), radius).toFixed(3))
      }
    })
  }
  // Leaving the area: back to the stylesheet (normal size, the selected entry enlarged).
  const leave = () => {
    cancelAnimationFrame(frame)
    for (const el of entries()) el.style.removeProperty('--nav-zoom')
  }
  area.addEventListener('pointermove', move)
  area.addEventListener('pointerleave', leave)
  return () => {
    cancelAnimationFrame(frame)
    area.removeEventListener('pointermove', move)
    area.removeEventListener('pointerleave', leave)
    leave()
  }
}
