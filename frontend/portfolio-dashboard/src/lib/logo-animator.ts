import { useLayoutEffect, type RefObject } from 'react'
import { LogoMotion } from '@/lib/logo-motion'

/*
 * The Matrix mark's one-time arrival (lib/logo-motion.ts, D-58): wherever the mark appears, its dots fly in once, then it
 * stays still. The mark never moves while the app works – that is the dot wave's job (`MatrixLoader`, D-59). Frames are
 * written imperatively (position and radius attributes on the circles, never React state – like the map camera); with
 * reduced motion the mark simply shows.
 */

const reducedMotion = () => typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches

interface Circle { el: SVGCircleElement; i: number; r: number; x: string; y: string }

/** The mark's circles with their dot index (`data-i`) and their place and radius in the still mark (`data-x/y/r`). */
function collect(svg: SVGSVGElement): Circle[] {
  return [...svg.querySelectorAll<SVGCircleElement>('circle[data-i]')].map((el) => ({
    el, i: Number(el.dataset.i), r: Number(el.dataset.r), x: el.dataset.x ?? '', y: el.dataset.y ?? '',
  }))
}

function restore(circles: Circle[]) {
  for (const c of circles) {
    c.el.setAttribute('cx', c.x)
    c.el.setAttribute('cy', c.y)
    c.el.setAttribute('r', String(c.r))
  }
}

/**
 * The one-time arrival of the mark in `ref`: every dot flies in once, then the mark stays still. Hidden before the first
 * paint, so the finished mark never flashes first. Never with reduced motion.
 */
export function useLogoIntro(ref: RefObject<SVGSVGElement | null>, enabled: boolean) {
  useLayoutEffect(() => {
    const svg = ref.current
    if (!enabled || !svg || reducedMotion() || typeof requestAnimationFrame !== 'function') return
    const circles = collect(svg)
    const intro = new LogoMotion()
    const startedAt = performance.now()
    let id = 0
    const step = () => {
      const t = performance.now() - startedAt
      if (t >= intro.introMs) { restore(circles); return }
      const frame = intro.introFrame(t)
      for (const c of circles) {
        c.el.setAttribute('cx', frame.x[c.i].toFixed(2))
        c.el.setAttribute('cy', frame.y[c.i].toFixed(2))
        c.el.setAttribute('r', (c.r * frame.scale[c.i]).toFixed(2))
      }
      id = requestAnimationFrame(step)
    }
    step()
    return () => {
      cancelAnimationFrame(id)
      restore(circles)
    }
  }, [ref, enabled])
}
