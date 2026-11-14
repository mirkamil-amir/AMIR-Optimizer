import { useLayoutEffect, type RefObject } from 'react'
import { cubeBalls, GRID_POSE, type CubePose } from '@/lib/cube'
import { prefersReducedMotion } from '@/lib/intro'
import { ThinkingMark } from '@/lib/thinking-mark'

/*
 * Draws the Matrix mark (lib/cube.ts, lib/thinking-mark.ts, D-62) into SVGs whose `circle[data-ball]` elements React
 * rendered once. Frames are written imperatively – attributes, never React state, like the map camera – by one animation
 * frame loop for every mark on the page, which stops as soon as all marks rest.
 *
 * "The app is thinking" is the work register below: every `MatrixLoader` and `WorkingPanel` on screen counts as work in
 * progress. While anything is registered the logo opens into the round ball and tumbles; when the last piece of work ends
 * it settles back to the flat grid. With reduced motion nothing moves (the logo stays the grid, loaders a still ball).
 */

let work = 0
/** Registers work in progress; call the returned function when it ends. */
export function beginWork(): () => void {
  work++
  wake()
  let ended = false
  return () => {
    if (ended) return
    ended = true
    work--
    wake()
  }
}
/** Whether the app is thinking right now. */
export const isWorking = () => work > 0

/** Writes `pose` into the SVG's balls (drawing order back to front). */
export function paintMark(svg: SVGSVGElement, pose: CubePose) {
  const circles = svg.querySelectorAll<SVGCircleElement>('circle[data-ball]')
  cubeBalls(pose).forEach((b, i) => {
    const c = circles[i]
    if (!c) return
    c.setAttribute('cx', String(b.x))
    c.setAttribute('cy', String(b.y))
    c.setAttribute('r', String(b.r))
    c.setAttribute('fill-opacity', String(b.o))
  })
}

interface Entry { svg: SVGSVGElement; mark: ThinkingMark; wants: () => boolean; resting: boolean }
const entries = new Set<Entry>()
let frame = 0
let last = 0

const canAnimate = () => !prefersReducedMotion() && typeof requestAnimationFrame === 'function'

function tick() {
  const now = performance.now()
  const dt = Math.min(50, now - last)
  last = now
  let busy = false
  for (const e of entries) {
    const wants = e.wants()
    if (!wants && !e.mark.moving) {
      if (!e.resting) { paintMark(e.svg, GRID_POSE); e.resting = true }
      continue
    }
    e.resting = false
    paintMark(e.svg, e.mark.step(dt, wants))
    busy = true
  }
  frame = busy ? requestAnimationFrame(tick) : 0
}

/** Starts the loop if it is not running (new mark, or work began or ended). */
function wake() {
  if (frame || !entries.size || !canAnimate()) return
  last = performance.now()
  frame = requestAnimationFrame(tick)
}

/**
 * Animates the mark in `ref`: it thinks (opens into the round ball and tumbles) while `wants()` is true and settles back
 * to the flat grid when it turns false.
 */
export function useThinkingMark(ref: RefObject<SVGSVGElement | null>, wants: () => boolean, enabled = true) {
  useLayoutEffect(() => {
    const svg = ref.current
    if (!svg || !enabled || !canAnimate()) return
    const entry: Entry = { svg, mark: new ThinkingMark(), wants, resting: true }
    entries.add(entry)
    wake()
    return () => {
      entries.delete(entry)
      paintMark(svg, GRID_POSE)
      if (!entries.size && frame) { cancelAnimationFrame(frame); frame = 0 }
    }
    // `wants` is read on every frame; a new function identity must not restart the mark.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ref, enabled])
}

/** Counts the calling component as work in progress while it is mounted. */
export function useWorkInProgress() {
  useLayoutEffect(() => beginWork(), [])
}
