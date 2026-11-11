import { LOGO_DOT_RADIUS, logoDots } from '@/lib/logo-dots'
import { cn } from '@/lib/utils'

/** The dot wave's dots are the logo's: the same 3 × 3 grid, lit along the diagonal (step 0 top left … 4 bottom right). */
const WAVE = logoDots()

/**
 * The app is thinking (D-59, owner's choice 2026-11-11): the dot wave – a 3 × 3 grid of dots with a soft wave of light
 * sweeping across it diagonally, again and again (CSS `.dot-wave`, no JavaScript). Never a spinner. Its dots are the
 * logo's (`lib/logo-dots.ts`): the logo is the dot wave at rest, and stays still in the top-left corner while the app works. Inherits the text colour and the size of an
 * icon (`size-4`, or the `size-*` of the surrounding button); reads down to 12 px. With reduced motion the dots stand still
 * in a fixed light pattern.
 */
export function MatrixLoader({ className, label }: { className?: string; label?: string }) {
  return (
    <svg viewBox="0 0 100 100" className={cn('dot-wave size-4 shrink-0', className)} role={label ? 'img' : undefined}
      aria-label={label} aria-hidden={label ? undefined : true} data-testid="dot-wave">
      {WAVE.map((d, i) => (
        <circle key={i} cx={d.x} cy={d.y} r={LOGO_DOT_RADIUS} fill="currentColor" style={{ animationDelay: `${(d.step * 0.14).toFixed(2)}s` }} />
      ))}
    </svg>
  )
}

/**
 * A running background job (download, ML training, backtest, comparison): the dot wave, what is happening, and – when
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
