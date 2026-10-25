import { useId } from 'react'
import { cn } from '@/lib/utils'

/** MaTrix (spelled so by the owner): named after the covariance/correlation matrix at the core of the optimizer and the logo. */
export const BRAND = {
  name: 'MaTrix',
  tagline: 'Portfolio Viewer',
} as const

/** Opacity of an off-diagonal dot: correlation fades with the distance from the diagonal. */
const DOT_OPACITY = [1, 0.85, 0.55, 0.3]

/**
 * The Matrix mark (chosen by the owner from nine concepts): a 4×4 correlation matrix – the risk model at the core of the
 * optimizer – whose diagonal of ones is set in the accent colour, on a softly graded tile. Colours follow the active skin:
 * --brand (tile), --brand-foreground (dots), --brand-accent (diagonal). `public/favicon.svg` is the same drawing.
 */
export function MatrixMark({ className, title = BRAND.name }: { className?: string; title?: string }) {
  const id = useId().replace(/:/g, '')
  const dots = [0, 1, 2, 3].flatMap((row) => [0, 1, 2, 3].map((col) => ({ row, col, diagonal: row === col })))
  return (
    <svg viewBox="0 0 40 40" className={cn('size-8 shrink-0', className)} role="img" aria-label={title}>
      <defs>
        <linearGradient id={`matrix-tile-${id}`} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" style={{ stopColor: 'color-mix(in oklch, var(--brand) 88%, white)' }} />
          <stop offset="1" style={{ stopColor: 'color-mix(in oklch, var(--brand) 82%, black)' }} />
        </linearGradient>
      </defs>
      <rect x="1" y="1" width="38" height="38" rx="10" fill={`url(#matrix-tile-${id})`} />
      {dots.map(({ row, col, diagonal }) => (
        <circle key={`${row}-${col}`} cx={11 + col * 6} cy={11 + row * 6} r={diagonal ? 2.4 : 1.9}
          fill={diagonal ? 'var(--brand-accent)' : 'var(--brand-foreground)'} fillOpacity={DOT_OPACITY[Math.abs(row - col)]} />
      ))}
    </svg>
  )
}

/** Mark with the name (Sora); the full version adds the tagline. */
export function MatrixWordmark({ compact = false, className }: { compact?: boolean; className?: string }) {
  return (
    <div className={cn('flex items-center gap-2.5', className)} title={`${BRAND.name} – ${BRAND.tagline}`}>
      <MatrixMark className={compact ? 'size-9' : 'size-11'} />
      <div className="leading-none">
        <div className={cn('font-brand font-semibold tracking-[0.01em]', compact ? 'text-[20px]' : 'text-[24px]')}>
          {BRAND.name}
        </div>
        {!compact && <div className="mt-1.5 text-[11px] text-muted-foreground">{BRAND.tagline}</div>}
      </div>
    </div>
  )
}
