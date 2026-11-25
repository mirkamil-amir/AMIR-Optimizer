import { useRef, useState, type CSSProperties, type ReactNode } from 'react'
import { DEFAULT_SPLIT, loadSplit, MAX_SPLIT, MIN_SPLIT, saveSplit, splitAfterKey, splitAt } from '@/lib/split'
import { cn } from '@/lib/utils'

/**
 * Two cards side by side on wide screens (stacked below `xl`) with a handle on the edge between them: hovering shows a
 * resize cursor, dragging moves the edge left or right (25–75 %), double-click or Enter resets it, arrow keys move it in
 * 5 % steps while the handle has focus. While dragging, the width is set imperatively (no re-render per pointer move);
 * the result is kept in state and remembered in this browser.
 */
export function SplitRow({ left, right, label }: { left: ReactNode; right: ReactNode; label: string }) {
  const rowRef = useRef<HTMLDivElement>(null)
  const [split, setSplit] = useState(loadSplit)
  const [dragging, setDragging] = useState(false)
  const live = useRef(split)

  const apply = (value: number) => {
    live.current = value
    rowRef.current?.style.setProperty('--split-left', `${value}fr`)
    rowRef.current?.style.setProperty('--split-right', `${1 - value}fr`)
  }
  const commit = (value: number) => {
    apply(value)
    setSplit(value)
    saveSplit(value)
  }

  const style = { '--split-left': `${split}fr`, '--split-right': `${1 - split}fr` } as CSSProperties

  return (
    <div ref={rowRef} style={style}
      className="grid items-start gap-5 xl:gap-0 xl:[grid-template-columns:minmax(0,var(--split-left))_1.25rem_minmax(0,var(--split-right))]">
      <div className="min-w-0">{left}</div>
      <div
        role="separator"
        tabIndex={0}
        aria-orientation="vertical"
        aria-label={label}
        aria-valuemin={Math.round(MIN_SPLIT * 100)}
        aria-valuemax={Math.round(MAX_SPLIT * 100)}
        aria-valuenow={Math.round(split * 100)}
        title="Drag to resize · double-click to reset"
        className="group hidden cursor-col-resize touch-none justify-center self-stretch outline-none xl:flex"
        onPointerDown={(e) => {
          if (e.button !== 0) return
          e.preventDefault()
          e.currentTarget.setPointerCapture(e.pointerId)
          setDragging(true)
        }}
        onPointerMove={(e) => {
          if (!dragging || !rowRef.current) return
          const r = rowRef.current.getBoundingClientRect()
          apply(splitAt(e.clientX, r.left, r.width))
        }}
        onPointerUp={(e) => {
          if (!dragging) return
          e.currentTarget.releasePointerCapture(e.pointerId)
          setDragging(false)
          commit(live.current)
        }}
        onPointerCancel={() => { setDragging(false); commit(live.current) }}
        onDoubleClick={() => commit(DEFAULT_SPLIT)}
        onKeyDown={(e) => {
          const next = splitAfterKey(split, e.key)
          if (next === null) return
          e.preventDefault()
          e.stopPropagation() // the panel's ←/→ switch stocks otherwise
          commit(next)
        }}
      >
        <span className={cn('my-2 w-1 rounded-full bg-border opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100',
          dragging && 'bg-primary opacity-100')} />
      </div>
      <div className="min-w-0">{right}</div>
    </div>
  )
}
