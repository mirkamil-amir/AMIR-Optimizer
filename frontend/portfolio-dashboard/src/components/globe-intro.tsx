import { Suspense, lazy, useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { useAuth } from '@/lib/auth'
import { INTRO_TIMING, markIntroSeen, prefersReducedMotion, shouldShowIntro } from '@/lib/intro'

// The globe and the country outlines it is drawn from load on demand, so they do not enlarge the initial bundle.
const GlobeCanvas = lazy(() => import('./globe-canvas'))

/**
 * Start-up intro (D-56): a dotted globe turning, with arcs between the exchange cities of the catalogue's markets – our
 * own drawing from the Natural Earth outlines – then it grows and fades into the app. Always dark. Plays once per browser
 * session while the app loads underneath (the app stays inert until it ends); click, Enter, Esc, Space or the Enter
 * button skips it. See `lib/intro.ts` for when it shows.
 */
export function IntroGate({ children }: { children: ReactNode }) {
  const [phase, setPhase] = useState<'show' | 'leaving' | 'done'>(() => (shouldShowIntro() ? 'show' : 'done'))
  const [reduced] = useState(prefersReducedMotion)
  const { isLoading } = useAuth()
  const startedAt = useRef(0)
  const leaving = useRef(false)

  useEffect(() => {
    startedAt.current = performance.now()
  }, [])

  const leave = useCallback(() => {
    if (leaving.current) return
    leaving.current = true
    setPhase('leaving')
    window.setTimeout(() => setPhase('done'), reduced ? INTRO_TIMING.reducedExitMs : INTRO_TIMING.exitMs)
  }, [reduced])

  useEffect(() => {
    if (phase === 'show') markIntroSeen()
  }, [phase])

  // Leaves once it has played its minimum and the sign-in check is done – or at the latest after the maximum.
  useEffect(() => {
    if (phase !== 'show') return
    const elapsed = performance.now() - startedAt.current
    const min = reduced ? INTRO_TIMING.reducedMinMs : INTRO_TIMING.minMs
    const wait = isLoading ? INTRO_TIMING.maxMs - elapsed : min - elapsed
    const timer = window.setTimeout(leave, Math.max(0, wait))
    return () => window.clearTimeout(timer)
  }, [phase, isLoading, reduced, leave])

  useEffect(() => {
    if (phase !== 'show') return
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Enter' || e.key === 'Escape' || e.key === ' ') {
        e.preventDefault()
        leave()
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [phase, leave])

  return (
    <>
      <div className="contents" inert={phase === 'show' ? true : undefined}>{children}</div>
      {phase !== 'done' && (
        <div role="dialog" aria-modal="true" aria-label="Matrix is starting" data-testid="globe-intro" data-phase={phase} onClick={leave}
          className="fixed inset-0 z-[100] cursor-pointer overflow-hidden ease-in"
          style={{
            background: 'radial-gradient(ellipse at 50% 42%, #0d1a3f 0%, #050913 58%, #020308 100%)',
            opacity: phase === 'leaving' ? 0 : 1,
            transform: phase === 'leaving' && !reduced ? 'scale(1.35)' : 'none',
            transition: `opacity ${reduced ? INTRO_TIMING.reducedExitMs : INTRO_TIMING.exitMs}ms, transform ${INTRO_TIMING.exitMs}ms`,
          }}>
          <Suspense fallback={null}><GlobeCanvas still={reduced} /></Suspense>
          <div className="absolute inset-x-0 bottom-[9%] flex flex-col items-center gap-4 px-4 text-center">
            <p className="heading-display text-3xl font-semibold tracking-[0.35em] text-white sm:text-4xl">MATRIX</p>
            <button type="button" autoFocus onClick={(e) => { e.stopPropagation(); leave() }}
              className="rounded-full border border-white/30 px-6 py-1.5 text-sm text-white/85 hover:bg-white/10 focus-visible:ring-2 focus-visible:ring-white/60 focus-visible:outline-none">
              Enter
            </button>
          </div>
        </div>
      )}
    </>
  )
}
