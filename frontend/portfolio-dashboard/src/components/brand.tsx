import { useId } from 'react'
import { cn } from '@/lib/utils'

/** AMIR = Allocation · Modelling · Investment · Research. */
export const BRAND = {
  short: 'AMIR',
  name: 'AMIR Optimizer',
  expansion: 'Allocation · Modelling · Investment · Research',
} as const

/**
 * The AMIR mark: a geometric "A" standing over three ascending bars (allocation and growth) on a softly graded tile with a
 * fine inner frame. Colours follow the active skin: --brand (tile), --brand-foreground (A), --brand-accent (bars, frame).
 */
export function AmirMark({ className, title = 'AMIR' }: { className?: string; title?: string }) {
  const id = useId().replace(/:/g, '')
  return (
    <svg viewBox="0 0 40 40" className={cn('size-8 shrink-0', className)} role="img" aria-label={title}>
      <defs>
        <linearGradient id={`amir-tile-${id}`} x1="0" y1="0" x2="1" y2="1">
          <stop offset="0" style={{ stopColor: 'color-mix(in oklch, var(--brand) 88%, white)' }} />
          <stop offset="1" style={{ stopColor: 'color-mix(in oklch, var(--brand) 82%, black)' }} />
        </linearGradient>
      </defs>
      <rect x="1" y="1" width="38" height="38" rx="10" fill={`url(#amir-tile-${id})`} />
      <rect x="3.5" y="3.5" width="33" height="33" rx="7.5" fill="none" stroke="var(--brand-accent)" strokeOpacity="0.5" strokeWidth="0.8" />
      <path d="M9.5 31 L18.4 8.5 L21.6 8.5 L30.5 31 L27.2 31 L20 12.6 L12.8 31 Z" fill="var(--brand-foreground)" />
      <rect x="16.4" y="28" width="2" height="3" rx="0.4" fill="var(--brand-accent)" />
      <rect x="19" y="25.6" width="2" height="5.4" rx="0.4" fill="var(--brand-accent)" />
      <rect x="21.6" y="23.2" width="2" height="7.8" rx="0.4" fill="var(--brand-accent)" />
    </svg>
  )
}

/** Mark with the name (serif, spaced) and the meaning of the acronym. */
export function AmirWordmark({ compact = false, className }: { compact?: boolean; className?: string }) {
  return (
    <div className={cn('flex items-center gap-2.5', className)} title={`${BRAND.name} – ${BRAND.expansion}`}>
      <AmirMark className={compact ? 'size-9' : 'size-11'} />
      <div className="leading-none">
        <div className={cn('font-brand font-semibold tracking-[0.18em]', compact ? 'text-[17px]' : 'text-[22px]')}>
          {BRAND.short}
        </div>
        <div className="mt-1 flex items-center gap-1.5">
          <span className="h-px w-3 bg-[var(--brand-accent)]" aria-hidden />
          <span className={cn('tracking-[0.14em] text-muted-foreground uppercase', compact ? 'text-[8.5px]' : 'text-[10px]')}>Optimizer</span>
        </div>
        {!compact && <div className="mt-1 text-[11px] text-muted-foreground">{BRAND.expansion}</div>}
      </div>
    </div>
  )
}
