import { cn } from '@/lib/utils'

/** Dot order of the sweep: row by row, like reading the matrix (the diagonal of the logo lights up on the way). */
const DOTS = [0, 1, 2, 3].flatMap((row) => [0, 1, 2, 3].map((col) => ({ row, col })))

/**
 * "Working" indicator in the shape of the MaTrix logo: the 16 dots of the correlation matrix light up one after another,
 * so a download, optimization or training visibly runs. Inherits the text colour and the size of an icon (1em, or the
 * `size-*` of the surrounding button). With reduced motion the dots pulse together slowly instead of sweeping.
 */
export function MatrixLoader({ className, label }: { className?: string; label?: string }) {
  return (
    <svg viewBox="0 0 40 40" className={cn('matrix-loader size-4 shrink-0', className)} role={label ? 'img' : undefined}
      aria-label={label} aria-hidden={label ? undefined : true}>
      {DOTS.map(({ row, col }, i) => (
        <circle key={i} cx={8 + col * 8} cy={8 + row * 8} r={row === col ? 3.6 : 3}
          fill="currentColor" style={{ animationDelay: `${i * 0.08}s` }} />
      ))}
    </svg>
  )
}

/**
 * A running background job (download, ML training, backtest, comparison): the large matrix loader in the brand colour,
 * what is happening, and – when known – the progress. `percent` null shows an indeterminate state.
 */
export function WorkingPanel({ title, detail, percent, className }: { title: string; detail?: string | null; percent?: number | null; className?: string }) {
  return (
    <div className={cn('flex items-center gap-4 rounded-xl border bg-card px-4 py-3', className)} role="status" aria-live="polite">
      <MatrixLoader className="size-9 text-[var(--brand)] dark:text-[var(--brand-accent)]" label={title} />
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
