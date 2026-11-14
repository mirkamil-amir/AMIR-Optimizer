import { useRef } from 'react'
import { CubeSvg } from '@/components/cube-svg'
import { GRID_POSE, STILL_BALL_POSE } from '@/lib/cube'
import { useThinkingMark, useWorkInProgress } from '@/lib/cube-animator'
import { prefersReducedMotion } from '@/lib/intro'
import { cn } from '@/lib/utils'

const always = () => true

/**
 * The app is thinking (D-62, owner's choice 2026-11-14): while a `MatrixLoader` or `WorkingPanel` is on screen the
 * Matrix logo in the top-left corner opens into the round ball and tumbles, and settles back to the 3 × 3 grid when the
 * work ends (`useWorkInProgress`, `lib/cube-animator.ts`). The loader itself shows the same small ball, tumbling – so a
 * button says it is busy even where the logo is out of sight (phone menu). Inherits the text colour and the size of an
 * icon (`size-4`, or the `size-*` of the surrounding button); with reduced motion a still ball. Never a spinner.
 */
export function MatrixLoader({ className, label }: { className?: string; label?: string }) {
  const ref = useRef<SVGSVGElement>(null)
  useWorkInProgress()
  useThinkingMark(ref, always)
  return (
    <CubeSvg ref={ref} pose={prefersReducedMotion() ? STILL_BALL_POSE : GRID_POSE} color="currentColor"
      className={cn('size-4 shrink-0', className)} role={label ? 'img' : undefined}
      aria-label={label} aria-hidden={label ? undefined : true} data-testid="thinking-ball" />
  )
}

/**
 * A running background job (download, ML training, backtest, comparison): the thinking ball, what is happening, and – when
 * known – the progress. `percent` null shows an indeterminate state.
 */
export function WorkingPanel({ title, detail, percent, className }: { title: string; detail?: string | null; percent?: number | null; className?: string }) {
  return (
    <div className={cn('flex items-center gap-4 rounded-xl border bg-card px-4 py-3', className)} role="status" aria-live="polite">
      <MatrixLoader className="size-9 text-[color:var(--mark-ink)]" label={title} />
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
