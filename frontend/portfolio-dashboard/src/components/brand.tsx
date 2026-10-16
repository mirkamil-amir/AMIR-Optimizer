import { cn } from '@/lib/utils'

/** AMIR = Allocation · Modelling · Investment · Research. */
export const BRAND = {
  short: 'AMIR',
  name: 'AMIR Optimizer',
  expansion: 'Allocation · Modelling · Investment · Research',
} as const

/**
 * The AMIR mark: an "A" whose crossbar is a rising return path ending in a highlighted point – the optimum on an efficient
 * frontier. Colours follow the active skin (--brand / --brand-foreground / --brand-accent).
 */
export function AmirMark({ className, title = 'AMIR' }: { className?: string; title?: string }) {
  return (
    <svg viewBox="0 0 32 32" className={cn('size-7 shrink-0', className)} role="img" aria-label={title}>
      <rect x="1" y="1" width="30" height="30" rx="8" fill="var(--brand)" />
      <path d="M8.5 24.5 L16 7.5 L23.5 24.5" fill="none" stroke="var(--brand-foreground)" strokeWidth="2.6" strokeLinecap="round" strokeLinejoin="round" />
      <path d="M10.6 19.6 L13.7 17.4 L16.6 18.4 L21.2 14.2" fill="none" stroke="var(--brand-accent)" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
      <circle cx="21.6" cy="13.9" r="1.9" fill="var(--brand-accent)" />
    </svg>
  )
}

/** Mark with the product name and the meaning of the acronym. */
export function AmirWordmark({ compact = false, className }: { compact?: boolean; className?: string }) {
  return (
    <div className={cn('flex items-center gap-2', className)}>
      <AmirMark className={compact ? 'size-7' : 'size-9'} />
      <div className="leading-tight">
        <div className={cn('font-semibold tracking-tight', compact ? 'text-sm' : 'text-lg')}>
          {BRAND.short}<span className="font-normal text-muted-foreground"> Optimizer</span>
        </div>
        <div className={cn('text-muted-foreground', compact ? 'text-[10.5px]' : 'text-xs')}>{BRAND.expansion}</div>
      </div>
    </div>
  )
}
