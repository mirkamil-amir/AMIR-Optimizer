import { useEffect, useSyncExternalStore, type RefObject } from 'react'
import { LOGO_FPS, LogoMotion } from '@/lib/logo-motion'

/*
 * Drives the Matrix mark's working animation (lib/logo-motion.ts). The logo moves only while the app is calculating:
 * every running-work indicator (`MatrixLoader`, `WorkingPanel`) registers itself with `useWorkInProgress`, and marks
 * that follow the work animate while at least one is registered; otherwise every mark stays still.
 *
 * One shared ticker at 20 frames per second updates all animated marks imperatively (attributes on their circles, never
 * React state – like the map camera). It stops when nothing is animated, when the browser tab is hidden and for marks
 * scrolled out of view; with reduced motion it never runs and the marks pulse gently in CSS instead (`.matrix-working`).
 */

// --- Work in progress -------------------------------------------------------------------------------------------------

let workCount = 0
const workListeners = new Set<() => void>()
const notifyWork = () => workListeners.forEach((listener) => listener())

/** Registers running work (download, optimization, training, backtest, comparison) while `active`. */
export function useWorkInProgress(active = true) {
  useEffect(() => {
    if (!active) return
    workCount++
    notifyWork()
    return () => {
      workCount--
      notifyWork()
    }
  }, [active])
}

/** True while any work indicator is on screen. */
export function useAppWorking() {
  return useSyncExternalStore(
    (listener) => { workListeners.add(listener); return () => workListeners.delete(listener) },
    () => workCount > 0,
    () => false,
  )
}

// --- Animated marks ---------------------------------------------------------------------------------------------------

interface Animated {
  circles: SVGCircleElement[]
  /** 'color': the full mark (foam turns a gold dot white); 'mono': the loader in the text colour (opacity only). */
  mode: 'color' | 'mono'
  visible: boolean
}

const animated = new Map<SVGSVGElement, Animated>()
let motion: LogoMotion | null = null
let startedAt = 0
let timer: ReturnType<typeof setInterval> | null = null
let observer: IntersectionObserver | null = null

const reducedMotion = () => typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches
const pageHidden = () => typeof document !== 'undefined' && document.visibilityState === 'hidden'

function tick() {
  if (!motion) return
  const t = (performance.now() - startedAt) / 1000
  motion.advance(t)
  const frame = motion.frame(t)
  for (const entry of animated.values()) {
    if (!entry.visible) continue
    entry.circles.forEach((circle, i) => {
      circle.setAttribute('fill-opacity', frame.opacity[i].toFixed(3))
      if (entry.mode === 'color') circle.setAttribute('fill', frame.foam[i] ? 'var(--brand-foreground)' : circle.dataset.fill ?? '')
    })
  }
}

/** Starts or stops the shared ticker to match what is on screen. */
function sync() {
  const run = !reducedMotion() && !pageHidden() && [...animated.values()].some((entry) => entry.visible)
  if (run && !timer) {
    if (!motion) {
      motion = new LogoMotion()
      startedAt = performance.now()
    }
    timer = setInterval(tick, 1000 / LOGO_FPS)
    tick()
  } else if (!run && timer) {
    clearInterval(timer)
    timer = null
  }
  // A fresh motion (fading in from the still mark) once nothing is animated any more.
  if (animated.size === 0) motion = null
}

if (typeof document !== 'undefined') document.addEventListener('visibilitychange', sync)

function restore(entry: Animated) {
  for (const circle of entry.circles) {
    circle.setAttribute('fill-opacity', circle.dataset.opacity ?? '1')
    if (entry.mode === 'color') circle.setAttribute('fill', circle.dataset.fill ?? '')
  }
}

function start(svg: SVGSVGElement, mode: Animated['mode']) {
  const entry: Animated = { circles: [...svg.querySelectorAll<SVGCircleElement>('circle[data-opacity]')], mode, visible: true }
  animated.set(svg, entry)
  if (typeof IntersectionObserver !== 'undefined') {
    observer ??= new IntersectionObserver((records) => {
      for (const record of records) {
        const found = animated.get(record.target as SVGSVGElement)
        if (found) found.visible = record.isIntersecting
      }
      sync()
    })
    observer.observe(svg)
  }
  sync()
  return () => {
    observer?.unobserve(svg)
    animated.delete(svg)
    restore(entry)
    sync()
  }
}

/** Animates the mark in `ref` while `active`; it shows the still mark otherwise. */
export function useLogoAnimation(ref: RefObject<SVGSVGElement | null>, active: boolean, mode: Animated['mode']) {
  useEffect(() => {
    const svg = ref.current
    if (!active || !svg) return
    return start(svg, mode)
  }, [ref, active, mode])
}
