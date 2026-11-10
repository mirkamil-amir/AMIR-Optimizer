import { Suspense, lazy, useCallback, useEffect, useRef, useState, type HTMLAttributes, type ReactNode } from 'react'
import { useAuth } from '@/lib/auth'
import { INTRO_TIMING, markIntroSeen, prefersReducedMotion, shouldShowIntro } from '@/lib/intro'
import { cn } from '@/lib/utils'
import type { GlobeLayout } from '@/lib/globe'

// The globe and the country outlines it is drawn from load on demand, so they do not enlarge the initial bundle.
const GlobeCanvas = lazy(() => import('./globe-canvas'))

/**
 * The dark globe screen (D-56): our own dotted globe with arcs between the catalogue's exchange cities, always dark,
 * full window, with the content on top. `leaving` grows and fades it out (into the app). Used by the start-up intro and
 * the sign-in page.
 */
export function GlobeScreen({ layout, leaving = false, className, children, ...rest }:
  { layout: GlobeLayout; leaving?: boolean; children: ReactNode } & HTMLAttributes<HTMLDivElement>) {
  const [reduced] = useState(prefersReducedMotion)
  return (
    <div {...rest} data-leaving={leaving || undefined} className={cn('fixed inset-0 z-[100] overflow-y-auto ease-in', className)}
      style={{
        background: 'radial-gradient(ellipse at 50% 42%, #0d1a3f 0%, #050913 58%, #020308 100%)',
        opacity: leaving ? 0 : 1,
        transform: leaving && !reduced ? 'scale(1.35)' : 'none',
        transition: `opacity ${reduced ? INTRO_TIMING.reducedExitMs : INTRO_TIMING.exitMs}ms, transform ${INTRO_TIMING.exitMs}ms`,
      }}>
      <Suspense fallback={null}><GlobeCanvas still={reduced} layout={layout} /></Suspense>
      {children}
    </div>
  )
}

/** The name on the globe screens. */
export function GlobeName({ className }: { className?: string }) {
  return <p className={cn('heading-display text-3xl font-semibold tracking-[0.35em] text-white sm:text-4xl', className)}>MATRIX</p>
}

/**
 * Start-up intro for signed-in users (D-56): the globe keeps turning until the user chooses Enter (owner, 2026-11-10),
 * once per browser session, while the app stays inert underneath. Signed-out users get the same globe as the sign-in page
 * instead (pages/auth.tsx), so they sign in right there. Decided once the sign-in check has answered; see `lib/intro.ts`.
 */
export function IntroGate({ children }: { children: ReactNode }) {
  const { user, isLoading } = useAuth()
  const [phase, setPhase] = useState<'pending' | 'show' | 'leaving' | 'done'>('pending')
  const [reduced] = useState(prefersReducedMotion)
  const leaving = useRef(false)

  // Decided once, as soon as the sign-in check has answered (state adjusted during render, not in an effect).
  if (phase === 'pending' && !isLoading) setPhase(user && shouldShowIntro() ? 'show' : 'done')

  useEffect(() => {
    if (phase === 'show') markIntroSeen()
  }, [phase])

  const leave = useCallback(() => {
    if (leaving.current) return
    leaving.current = true
    setPhase('leaving')
    window.setTimeout(() => setPhase('done'), reduced ? INTRO_TIMING.reducedExitMs : INTRO_TIMING.exitMs)
  }, [reduced])

  const showing = phase === 'show' || phase === 'leaving'
  return (
    <>
      <div className="contents" inert={phase === 'show' ? true : undefined}>{children}</div>
      {showing && (
        <GlobeScreen layout="center" leaving={phase === 'leaving'} role="dialog" aria-modal="true" aria-label="Matrix is starting"
          data-testid="globe-intro" data-phase={phase} className="overflow-hidden">
          <div className="absolute inset-x-0 bottom-[9%] flex flex-col items-center gap-4 px-4 text-center">
            <GlobeName />
            <button type="button" autoFocus onClick={leave}
              className="rounded-full border border-white/30 px-6 py-1.5 text-sm text-white/85 hover:bg-white/10 focus-visible:ring-2 focus-visible:ring-white/60 focus-visible:outline-none">
              Enter
            </button>
          </div>
        </GlobeScreen>
      )}
    </>
  )
}
