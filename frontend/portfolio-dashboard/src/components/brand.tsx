import { useId, useRef } from 'react'
import { useLogoIntro } from '@/lib/logo-animator'
import { LOGO_DOT_RADIUS, logoDots } from '@/lib/logo-dots'
import { cn } from '@/lib/utils'

/** Matrix (named by the owner; "MatrixView" until 2026-11-01): after the covariance/correlation matrix at the core of the optimizer and the logo. */
export const BRAND = {
  name: 'Matrix',
} as const

const DOTS = logoDots()

/**
 * The Matrix mark (owner's choice, 2026-11-11, D-58): a 3 × 3 grid of round dots – the thinking animation's dots at rest
 * (`lib/logo-dots.ts`; the dot wave of `MatrixLoader` uses the same dots). No frame: the dots stand on whatever is behind
 * them, coloured by `--mark-ink` – the skin's brand colour on light surfaces, the light text colour on dark ones – so the
 * mark always matches the app's background. Wherever it appears its dots fly in once (`intro`, `lib/logo-motion.ts`,
 * driven by `lib/logo-animator.ts`), then it stays still; it never moves while the app works (the dot wave shows that,
 * D-59). In the app it appears only in the top-left corner (`MatrixWordmark`); on the landing pages above the name
 * (`GlobeName`). `tile` puts it on a graded --brand tile (not used in the app; the favicon has one for browser tabs).
 * `decorative` hides it from assistive technology where the name follows anyway. `public/favicon.svg` is the still drawing.
 */
export function MatrixMark({ className, title = BRAND.name, tile = false, color = 'var(--mark-ink)', intro = true, decorative = false }:
  { className?: string; title?: string; tile?: boolean; color?: string; intro?: boolean; decorative?: boolean }) {
  const id = useId().replace(/:/g, '')
  const ref = useRef<SVGSVGElement>(null)
  useLogoIntro(ref, intro)
  return (
    <svg ref={ref} viewBox="0 0 100 100" className={cn('size-8 shrink-0', className)}
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
      {DOTS.map((d, i) => (
        <circle key={i} cx={d.x} cy={d.y} r={LOGO_DOT_RADIUS} fill={color} data-i={i} data-x={d.x} data-y={d.y} data-r={LOGO_DOT_RADIUS} />
      ))}
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
