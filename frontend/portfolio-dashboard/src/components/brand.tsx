import { useId, useRef } from 'react'
import { useAppWorking, useLogoAnimation } from '@/lib/logo-animator'
import { logoDots } from '@/lib/logo-dots'
import { cn } from '@/lib/utils'

/** Matrix (named by the owner; "MatrixView" until 2026-11-01): after the covariance/correlation matrix at the core of the optimizer and the logo. */
export const BRAND = {
  name: 'Matrix',
} as const

const DOTS = logoDots()

/**
 * The Matrix mark (variation 3c, chosen by the owner on 2026-11-01): an M glowing through an 11 × 11 dot matrix, its two
 * diagonals in the accent colour, on a softly graded tile (`lib/logo-dots.ts`). Colours follow the active skin: --brand
 * (tile), --brand-foreground (dots), --brand-accent (diagonals). `motion`: 'work' moves only while the app is calculating
 * (the shell's logo), 'always' moves for as long as it is shown (`WorkingPanel`), 'still' never moves. The movement is
 * `lib/logo-motion.ts`, driven by `lib/logo-animator.ts`. `public/favicon.svg` is the still drawing.
 */
export function MatrixMark({ className, title = BRAND.name, motion = 'work' }: { className?: string; title?: string; motion?: 'work' | 'always' | 'still' }) {
  const id = useId().replace(/:/g, '')
  const ref = useRef<SVGSVGElement>(null)
  const working = useAppWorking()
  const active = motion === 'always' || (motion === 'work' && working)
  useLogoAnimation(ref, active, 'color')
  return (
    <svg ref={ref} viewBox="0 0 100 100" className={cn('size-8 shrink-0', active && 'matrix-working', className)} role="img" aria-label={title}>
      <defs>
        <linearGradient id={`matrix-tile-${id}`} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" style={{ stopColor: 'color-mix(in oklch, var(--brand) 88%, white)' }} />
          <stop offset="1" style={{ stopColor: 'color-mix(in oklch, var(--brand) 82%, black)' }} />
        </linearGradient>
      </defs>
      <rect width="100" height="100" rx="22" fill={`url(#matrix-tile-${id})`} />
      {DOTS.map((d, i) => {
        const fill = d.gold ? 'var(--brand-accent)' : 'var(--brand-foreground)'
        return <circle key={i} cx={d.x} cy={d.y} r="2.5" fill={fill} fillOpacity={d.opacity} data-fill={fill} data-opacity={d.opacity} />
      })}
    </svg>
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
