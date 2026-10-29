import { useCallback, useRef, type CSSProperties, type ReactNode, type UIEventHandler } from 'react'
import { cn } from '@/lib/utils'

/** Pixels the mouse must move before a press becomes a drag (a plain click or focus stays a click). */
const DRAG_THRESHOLD = 4

/**
 * A box for content larger than the screen (e.g. a big correlation matrix): both scroll bars, and with a mouse the
 * content can be pulled around like a map – press, drag, release. Touch and pen keep the browser's own scrolling;
 * keyboard users scroll with the bars or move focus through the content.
 */
export function PannableArea({ children, className, style, label, viewportRef, onScroll }: {
  children: ReactNode; className?: string; style?: CSSProperties; label: string
  /** Receives the scrolling element, e.g. to draw only what is in view. */
  viewportRef?: (el: HTMLDivElement | null) => void
  onScroll?: UIEventHandler<HTMLDivElement>
}) {
  const ref = useRef<HTMLDivElement | null>(null)
  const setRef = useCallback((el: HTMLDivElement | null) => { ref.current = el; viewportRef?.(el) }, [viewportRef])
  const drag = useRef<{ x: number; y: number; left: number; top: number; moved: boolean } | null>(null)

  return (
    <div
      ref={setRef}
      onScroll={onScroll}
      role="region"
      aria-label={label}
      tabIndex={0}
      className={cn('cursor-grab overflow-auto overscroll-contain rounded-lg border bg-card outline-none select-none focus-visible:ring-2 focus-visible:ring-ring data-[dragging]:cursor-grabbing', className)}
      style={style}
      onPointerDown={(e) => {
        if (e.pointerType !== 'mouse' || e.button !== 0 || !ref.current) return
        drag.current = { x: e.clientX, y: e.clientY, left: ref.current.scrollLeft, top: ref.current.scrollTop, moved: false }
      }}
      onPointerMove={(e) => {
        const d = drag.current
        const el = ref.current
        if (!d || !el) return
        const dx = e.clientX - d.x
        const dy = e.clientY - d.y
        if (!d.moved && Math.hypot(dx, dy) < DRAG_THRESHOLD) return
        if (!d.moved) {
          d.moved = true
          el.dataset.dragging = ''
          el.setPointerCapture?.(e.pointerId)
        }
        el.scrollLeft = d.left - dx
        el.scrollTop = d.top - dy
      }}
      onPointerUp={(e) => endDrag(ref.current, drag, e.pointerId)}
      onPointerCancel={(e) => endDrag(ref.current, drag, e.pointerId)}
    >
      {children}
    </div>
  )
}

function endDrag(el: HTMLDivElement | null, drag: { current: unknown }, pointerId: number) {
  drag.current = null
  if (!el) return
  delete el.dataset.dragging
  if (el.hasPointerCapture?.(pointerId)) el.releasePointerCapture(pointerId)
}
