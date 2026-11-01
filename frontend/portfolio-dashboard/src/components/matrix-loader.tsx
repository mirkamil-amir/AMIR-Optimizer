import { useRef } from 'react'
import { MatrixMark } from '@/components/brand'
import { useLogoAnimation, useWorkInProgress } from '@/lib/logo-animator'
import { logoDots } from '@/lib/logo-dots'
import { cn } from '@/lib/utils'

const DOTS = logoDots()

/**
 * "Working" indicator in the shape of the Matrix logo: the glowing M of the 11 × 11 matrix with the logo's working
 * animation (surf, stones, tornadoes, twinkle – `lib/logo-motion.ts`), so a download, optimization or training visibly
 * runs. While it is shown the shell's logo moves too (`useWorkInProgress`). Inherits the text colour and the size of an
 * icon (1em, or the `size-*` of the surrounding button); dots are drawn a little larger than in the mark so the M stays
 * readable at 12–16 px. With reduced motion it pulses gently instead.
 */
export function MatrixLoader({ className, label }: { className?: string; label?: string }) {
  const ref = useRef<SVGSVGElement>(null)
  useWorkInProgress()
  useLogoAnimation(ref, true, 'mono')
  return (
    <svg ref={ref} viewBox="0 0 100 100" className={cn('matrix-working size-4 shrink-0', className)} role={label ? 'img' : undefined}
      aria-label={label} aria-hidden={label ? undefined : true}>
      {DOTS.map((d, i) => (
        <circle key={i} cx={d.x} cy={d.y} r="3.4" fill="currentColor" fillOpacity={d.opacity} data-opacity={d.opacity} />
      ))}
    </svg>
  )
}

/**
 * A running background job (download, ML training, backtest, comparison): the moving logo, what is happening, and – when
 * known – the progress. `percent` null shows an indeterminate state.
 */
export function WorkingPanel({ title, detail, percent, className }: { title: string; detail?: string | null; percent?: number | null; className?: string }) {
  useWorkInProgress()
  return (
    <div className={cn('flex items-center gap-4 rounded-xl border bg-card px-4 py-3', className)} role="status" aria-live="polite">
      <MatrixMark motion="always" className="size-9" title={title} />
      <div className="min-w-0 flex-1 space-y-1.5">
        <div className="flex items-baseline justify-between gap-3 text-sm">
          <span className="font-medium">{title}</span>
          {percent != null && <span className="text-xs text-muted-foreground tabular">{Math.round(percent)}%</span>}
        </div>
        {percent != null && (
          <div className="h-1.5 overflow-hidden rounded-full bg-muted" aria-hidden>
            <div className="h-full rounded-full bg-[var(--brand)] transition-[width] duration-500 dark:bg-[var(--brand-accent)]" style={{ width: `${Math.max(2, Math.min(100, percent))}%` }} />
          </div>
        )}
        {detail && <p className="truncate text-xs text-muted-foreground">{detail}</p>}
      </div>
    </div>
  )
}
