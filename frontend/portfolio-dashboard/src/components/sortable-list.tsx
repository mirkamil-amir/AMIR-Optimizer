import { useEffect, useId, useRef, useState, type ReactNode } from 'react'
import { GripVertical } from 'lucide-react'
import { dropIndex, shiftFor } from '@/lib/reorder'
import { cn } from '@/lib/utils'

interface Drag { from: number; to: number; startY: number; dy: number; mids: number[]; height: number; scrollStart: number }

/**
 * A vertical list the user reorders by dragging a row's handle (mouse, pen and touch – pointer events, so it works on
 * phones too) or with the arrow keys on the focused handle. Other rows slide aside while dragging; Escape cancels.
 * `onMove(from, to)` gets indexes within `items`; the caller saves the new order. Inside `scrollBox` (the element that
 * scrolls) a drag near its top or bottom edge scrolls it.
 */
export function SortableList<T>({ items, getKey, getLabel, onMove, renderItem, scrollBox, disabled = false }: {
  items: T[]
  getKey: (item: T) => string
  /** Name read out for the handle and announcements, e.g. the symbol. */
  getLabel: (item: T) => string
  onMove: (from: number, to: number) => void
  renderItem: (item: T, handle: ReactNode) => ReactNode
  scrollBox?: HTMLElement | null
  disabled?: boolean
}) {
  const [drag, setDrag] = useState<Drag | null>(null)
  const [announcement, setAnnouncement] = useState('')
  const rows = useRef(new Map<string, HTMLElement>())
  const handles = useRef(new Map<string, HTMLButtonElement>())
  const refocus = useRef<string | null>(null)
  const helpId = useId()

  // Keyboard moves re-render the list; keep the focus on the handle of the row that moved.
  useEffect(() => {
    if (!refocus.current) return
    handles.current.get(refocus.current)?.focus()
    refocus.current = null
  })

  const start = (e: React.PointerEvent<HTMLButtonElement>, index: number) => {
    if (disabled || e.button !== 0) return
    e.preventDefault()
    e.currentTarget.setPointerCapture(e.pointerId)
    const rects = items.map((it) => rows.current.get(getKey(it))?.getBoundingClientRect())
    if (rects.some((r) => !r)) return
    setDrag({
      from: index, to: index, startY: e.clientY, dy: 0, height: rects[index]!.height + ROW_GAP,
      mids: rects.map((r) => r!.top + r!.height / 2), scrollStart: scrollBox?.scrollTop ?? 0,
    })
  }

  const move = (e: React.PointerEvent<HTMLButtonElement>) => {
    if (!drag) return
    // Near the edges of the scrolling box, scroll it so rows further away can be reached.
    if (scrollBox) {
      const box = scrollBox.getBoundingClientRect()
      if (e.clientY < box.top + EDGE) scrollBox.scrollBy(0, -SCROLL_STEP)
      else if (e.clientY > box.bottom - EDGE) scrollBox.scrollBy(0, SCROLL_STEP)
    }
    const scrolled = (scrollBox?.scrollTop ?? 0) - drag.scrollStart
    const dy = e.clientY - drag.startY + scrolled
    const center = drag.mids[drag.from] + dy - scrolled
    setDrag({ ...drag, dy, to: dropIndex(drag.mids.map((m) => m - scrolled), drag.from, center) })
  }

  const end = (commit: boolean) => {
    if (!drag) return
    if (commit && drag.to !== drag.from) {
      onMove(drag.from, drag.to)
      setAnnouncement(`${getLabel(items[drag.from])} moved to position ${drag.to + 1} of ${items.length}.`)
    }
    setDrag(null)
  }

  const keys = (e: React.KeyboardEvent<HTMLButtonElement>, index: number) => {
    if (e.key === 'Escape' && drag) { e.preventDefault(); end(false); return }
    if (disabled) return
    const to = e.key === 'ArrowUp' ? index - 1 : e.key === 'ArrowDown' ? index + 1 : e.key === 'Home' ? 0 : e.key === 'End' ? items.length - 1 : null
    if (to === null || to < 0 || to >= items.length || to === index) return
    e.preventDefault()
    refocus.current = getKey(items[index])
    onMove(index, to)
    setAnnouncement(`${getLabel(items[index])} moved to position ${to + 1} of ${items.length}.`)
  }

  return (
    <>
      <p id={helpId} className="sr-only">Drag the handle, or focus it and use the arrow keys (Home and End for top and bottom), to change the order.</p>
      <ul className="space-y-2" aria-label="Watched stocks in your order">
        {items.map((item, index) => {
          const key = getKey(item)
          const dragging = drag?.from === index
          const offset = drag ? (dragging ? drag.dy : shiftFor(index, drag.from, drag.to, drag.height)) : 0
          const handle = (
            <button
              type="button"
              ref={(el) => { if (el) handles.current.set(key, el); else handles.current.delete(key) }}
              aria-label={`Move ${getLabel(item)} (position ${index + 1} of ${items.length})`}
              aria-describedby={helpId}
              disabled={disabled}
              onPointerDown={(e) => start(e, index)}
              onPointerMove={move}
              onPointerUp={() => end(true)}
              onPointerCancel={() => end(false)}
              onKeyDown={(e) => keys(e, index)}
              className={cn('grid size-8 shrink-0 touch-none place-items-center rounded-md text-muted-foreground outline-none hover:bg-muted hover:text-foreground focus-visible:ring-2 focus-visible:ring-ring/50 disabled:opacity-40',
                dragging ? 'cursor-grabbing' : 'cursor-grab')}
            >
              <GripVertical className="size-4" aria-hidden />
            </button>
          )
          return (
            <li
              key={key}
              ref={(el) => { if (el) rows.current.set(key, el); else rows.current.delete(key) }}
              className={cn('relative', dragging ? 'z-10 rounded-xl bg-popover shadow-lg' : drag && 'transition-transform duration-150')}
              style={offset ? { transform: `translateY(${offset}px)` } : undefined}
            >
              {renderItem(item, handle)}
            </li>
          )
        })}
      </ul>
      <p className="sr-only" aria-live="polite">{announcement}</p>
    </>
  )
}

/** Vertical gap between rows (`space-y-2`), so neighbours slide by exactly one row. */
const ROW_GAP = 8
const EDGE = 48
const SCROLL_STEP = 12
