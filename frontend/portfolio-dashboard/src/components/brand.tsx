import { useId, useRef } from 'react'
import { CubeSvg } from '@/components/cube-svg'
import { GRID_POSE } from '@/lib/cube'
import { isWorking, useThinkingMark } from '@/lib/cube-animator'
import { cn } from '@/lib/utils'

/** Matrix (named by the owner; "MatrixView" until 2026-11-01): after the covariance/correlation matrix at the core of the optimizer and the logo. */
export const BRAND = {
  name: 'Matrix',
} as const

/** On arrival the mark thinks this long once, then settles into the grid. */
export const ARRIVAL_MS = 700

/**
 * The Matrix mark (owner's choice, 2026-11-14, D-62): a 3 × 3 grid of dots – the front of a 3 × 3 × 3 cube of 27 balls
 * (`lib/cube.ts`). It is the app's thinking indicator: while any work is in progress (a `MatrixLoader` or `WorkingPanel`
 * on screen) it opens into a round ball like the start-up globe and tumbles while its layers twist; when the work ends it
 * untwists and closes back into the grid (`lib/thinking-mark.ts`, drawn by `lib/cube-animator.ts`). On arrival it thinks
 * once briefly (`intro`). With reduced motion it stays the grid. No frame: the dots stand on whatever is behind them,
 * coloured by `--mark-ink` – the skin's brand colour on light surfaces, the light text colour on dark ones. In the app it
 * appears only in the top-left corner (`MatrixWordmark`); on the landing pages above the name (`GlobeName`). `tile` puts
 * it on a graded --brand tile (not used in the app). `decorative` hides it from assistive technology where the name
 * follows anyway. `public/favicon.svg` is the still grid.
 */
export function MatrixMark({ className, title = BRAND.name, tile = false, color = 'var(--mark-ink)', intro = true, decorative = false }:
  { className?: string; title?: string; tile?: boolean; color?: string; intro?: boolean; decorative?: boolean }) {
  const id = useId().replace(/:/g, '')
  const ref = useRef<SVGSVGElement>(null)
  const born = useRef<number | null>(null)
  useThinkingMark(ref, () => {
    if (isWorking()) return true
    if (!intro) return false
    born.current ??= performance.now()
    return performance.now() - born.current < ARRIVAL_MS
  })
  return (
    <CubeSvg ref={ref} pose={GRID_POSE} color={color} className={cn('size-8 shrink-0', className)}
      role={decorative ? undefined : 'img'} aria-label={decorative ? undefined : title} aria-hidden={decorative || undefined}>
      {tile && (
        <>
          <defs>
            <linearGradient id={`matrix-tile-${id}`} x1="0" y1="0" x2="1" y2="1">
              <stop offset="0" style={{ stopColor: 'color-mix(in oklch, var(--brand) 88%, white)' }} />
              <stop offset="1" style={{ stopColor: 'color-mix(in oklch, var(--brand) 82%, black)' }} />
            </linearGradient>
          </defs>
          <rect width="100" height="100" rx="22" fill={`url(#matrix-tile-${id})`} />
        </>
      )}
    </CubeSvg>
  )
}

/** Mark with the name (Sora); `compact` is the smaller size of the top bar. */
export function MatrixWordmark({ compact = false, className }: { compact?: boolean; className?: string }) {
  return (
    <div className={cn('flex items-center gap-2.5', className)} title={BRAND.name}>
      <MatrixMark className={compact ? 'size-9' : 'size-11'} />
      <div className="leading-none">
        <div className={cn('font-brand font-semibold tracking-[0.01em]', compact ? 'text-[20px]' : 'text-[24px]')}>
          {BRAND.name}
        </div>
      </div>
    </div>
  )
}
