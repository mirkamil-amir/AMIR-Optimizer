import { useId, useState, type ReactNode } from 'react'
import { ChevronDown } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import type { Metric } from '@/lib/types'
import { cn } from '@/lib/utils'

/**
 * Page title. Inside a portfolio workspace (whose name is the page's h1) sections pass `level={2}`. What a page is for goes
 * into `info` (a tip beside the title); `description` is kept for facts about the shown data and required caveats.
 */
export function PageHeader({ title, description, info, actions, level = 1 }: {
  title: string; description?: ReactNode; info?: ReactNode; actions?: ReactNode; level?: 1 | 2
}) {
  const Heading = level === 1 ? 'h1' : 'h2'
  return (
    <div className="mb-6 flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
      <div>
        <div className="flex items-center gap-2">
          <Heading className={cn('heading-display', level === 1 ? 'text-[1.7rem]' : 'text-[1.3rem]')}><InfoTip of={title}>{info}</InfoTip></Heading>
        </div>
        {description && <p className="mt-1 text-sm text-muted-foreground">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}

/**
 * An explanation shown on hover or keyboard focus – used for every financial metric. No icon and no underline: with `of`
 * the label itself (a title, a field name) is the trigger; without it a small "About" text is. Without an
 * explanation only `of` is rendered.
 */
export function InfoTip({ children, of, label = 'More information' }: { children?: ReactNode; of?: ReactNode; label?: string }) {
  if (!children) return <>{of}</>
  return (
    <Tooltip>
      <TooltipTrigger
        render={of
          ? <button type="button" className="cursor-help text-left focus-visible:outline-2" />
          : <button type="button" aria-label={label} className="cursor-help text-xs font-normal text-muted-foreground hover:text-foreground focus-visible:outline-2" />}
      >
        {of ?? 'About'}
      </TooltipTrigger>
      <TooltipContent className="max-w-xs text-xs leading-relaxed">{children}</TooltipContent>
    </Tooltip>
  )
}

/**
 * A titled row of key figures (four per row on wide screens). Pages tell a story with consecutive groups – e.g. value →
 * performance → risk – and put each group's basis in its tip; `period` states the dates the figures cover.
 */
export function MetricGroup({ title, hint, period, columns = 4, children }: {
  title: string; hint?: ReactNode; period?: ReactNode; columns?: 4 | 5; children: ReactNode
}) {
  return (
    <section aria-label={title} className="space-y-2">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <h3 className="flex items-center gap-1.5 text-sm font-medium"><InfoTip of={title}>{hint}</InfoTip></h3>
        {period && <span className="text-xs text-muted-foreground tabular">{period}</span>}
      </div>
      <div className={cn('grid grid-cols-2 gap-3', columns === 5 ? 'md:grid-cols-3 xl:grid-cols-5' : 'md:grid-cols-4')}>{children}</div>
    </section>
  )
}

export type Tone = 'neutral' | 'signed' | 'inverse'

/** Compact metric card. `tone="signed"` colours by sign (with arrow), never by colour alone. */
export function MetricCard({
  label, value, sub, hint, tone = 'neutral', numeric, loading, className,
}: {
  label: string
  value: ReactNode
  sub?: ReactNode
  hint?: ReactNode
  tone?: Tone
  numeric?: number | null
  loading?: boolean
  className?: string
}) {
  const sign = numeric === null || numeric === undefined || numeric === 0 ? 0 : numeric > 0 ? 1 : -1
  const good = tone === 'inverse' ? -sign : sign
  return (
    <Card className={cn('gap-0 py-4', className)}>
      <CardContent className="px-4">
        <div className="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
          <InfoTip of={label}>{hint}</InfoTip>
        </div>
        {loading ? (
          <Skeleton className="mt-2 h-7 w-28" />
        ) : (
          <div className={cn('mt-1.5 flex items-center gap-1 text-xl font-semibold', tone !== 'neutral' && good > 0 && 'text-gain', tone !== 'neutral' && good < 0 && 'text-loss')}>
            {value}
          </div>
        )}
        {sub && <div className="mt-1 text-xs text-muted-foreground">{sub}</div>}
      </CardContent>
    </Card>
  )
}

/** Renders a metric that may be unavailable (with the reason in a tooltip) instead of NaN. */
export function MetricValue({ metric, format }: { metric: Metric | undefined; format: (v: number) => string }) {
  if (!metric) return <span>—</span>
  if (metric.value === null) {
    return (
      <Tooltip>
        <TooltipTrigger render={<span className="cursor-help text-muted-foreground" />}>n/a</TooltipTrigger>
        <TooltipContent className="max-w-xs text-xs">{metric.unavailableReason}</TooltipContent>
      </Tooltip>
    )
  }
  return <span>{format(metric.value)}</span>
}

export type DataKind = 'live' | 'cached' | 'stale' | 'simulated' | 'backtest' | 'estimate' | 'missing'

const dataKindStyles: Record<DataKind, { label: string; className: string; title: string }> = {
  live: { label: 'Market data', className: 'border-gain/40 text-gain', title: 'Real market data (delayed or end-of-day, not live).' },
  cached: { label: 'Cached', className: 'border-border text-muted-foreground', title: 'Stored market data from the last synchronization.' },
  stale: { label: 'Stale', className: 'border-warn/50 text-warn', title: 'The latest price is older than the staleness threshold.' },
  simulated: { label: 'SIMULATED', className: 'border-series-2/60 bg-series-2/10 text-foreground', title: 'Synthetic prices – not real market data.' },
  backtest: { label: 'Backtest', className: 'border-series-7/50 text-foreground', title: 'Hypothetical historical simulation – not a forecast.' },
  estimate: { label: 'Estimate', className: 'border-border text-muted-foreground', title: 'Historical estimate used as model input – not a forecast.' },
  missing: { label: 'No data', className: 'border-loss/40 text-loss', title: 'No price data stored for this asset.' },
}

export function DataBadge({ kind, label }: { kind: DataKind; label?: string }) {
  const s = dataKindStyles[kind]
  return (
    <Badge variant="outline" title={s.title} className={cn('font-medium', s.className)}>
      {label ?? s.label}
    </Badge>
  )
}

export function EmptyState({ title, description, action }: { title: string; description?: ReactNode; action?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center rounded-xl border border-dashed px-6 py-12 text-center">
      <h3 className="font-medium">{title}</h3>
      {description && <p className="mt-1 max-w-md text-sm text-muted-foreground">{description}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  )
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  return (
    <Alert variant="destructive">
      <AlertTitle>Something went wrong</AlertTitle>
      <AlertDescription>
        <p>{errorMessage(error)}</p>
        {onRetry && (
          <Button size="sm" variant="outline" className="mt-2" onClick={onRetry}>
            Retry
          </Button>
        )}
      </AlertDescription>
    </Alert>
  )
}

/**
 * Data and methodology notes. `collapsible`: closed by default (title and count); opening it shows every note, untruncated.
 */
export function Warnings({ items, title = 'Data notes', collapsible = false }: { items: string[] | undefined; title?: string; collapsible?: boolean }) {
  if (!items || items.length === 0) return null
  if (collapsible) {
    return (
      <Collapsible title={title} summary={`${items.length} ${items.length === 1 ? 'note' : 'notes'}`} className="ring-warn/40">
        <ul className="list-disc space-y-0.5 pl-5 text-muted-foreground">
          {items.map((w) => <li key={w}>{w}</li>)}
        </ul>
      </Collapsible>
    )
  }
  return (
    <Alert className="border-warn/40">
      <AlertTitle>{title}</AlertTitle>
      <AlertDescription>
        <ul className="list-disc space-y-0.5 pl-4">
          {items.slice(0, 8).map((w) => (
            <li key={w}>{w}</li>
          ))}
          {items.length > 8 && <li>… and {items.length - 8} more</li>}
        </ul>
      </AlertDescription>
    </Alert>
  )
}

export function LoadingBlock({ height = 'h-64' }: { height?: string }) {
  return <Skeleton className={cn('w-full rounded-xl', height)} />
}

/**
 * A card whose content is folded away until its title is clicked (closed by default). The title row stays visible with
 * an optional summary and actions; the toggle is a button with `aria-expanded`, so it works with keyboard and screen readers.
 */
export function Collapsible({ title, summary, icon, hint, actions, defaultOpen = false, className, children }: {
  title: ReactNode; summary?: ReactNode; icon?: ReactNode; hint?: ReactNode; actions?: ReactNode; defaultOpen?: boolean; className?: string; children: ReactNode
}) {
  const [open, setOpen] = useState(defaultOpen)
  const id = useId()
  return (
    <div className={cn('rounded-xl bg-card text-sm ring-1 ring-foreground/10', className)}>
      <div className="flex items-center gap-2 px-4 py-3">
        <button type="button" aria-expanded={open} aria-controls={id} onClick={() => setOpen((o) => !o)}
          className="flex min-w-0 flex-1 items-center gap-2 text-left font-semibold outline-none focus-visible:ring-2 focus-visible:ring-ring/50">
          <ChevronDown className={cn('size-4 shrink-0 text-muted-foreground transition-transform', open && 'rotate-180')} aria-hidden />
          {icon}
          <span className="truncate">{title}</span>
          {summary && <span className="shrink-0 text-xs font-normal text-muted-foreground">({summary})</span>}
        </button>
        {hint && <InfoTip>{hint}</InfoTip>}
        {actions}
      </div>
      {open && <div id={id} className="px-4 pb-4">{children}</div>}
    </div>
  )
}

/**
 * One result section of an optimization page (Math and ML Optimization use the same layout): a heading and its cards,
 * visible at once – results are never hidden behind tabs. `collapsible`: reference material (inputs, methodology) folded
 * until its heading is clicked.
 */
export function ResultSection({ title, hint, collapsible = false, children }: { title: string; hint?: ReactNode; collapsible?: boolean; children: ReactNode }) {
  const [open, setOpen] = useState(false)
  const id = useId()
  if (collapsible) {
    return (
      <section className="space-y-3" aria-label={title}>
        <h3 className="heading-display flex items-center gap-1.5 border-b pb-1 text-[1.15rem]">
          <button type="button" aria-expanded={open} aria-controls={id} onClick={() => setOpen((o) => !o)} className="flex items-center gap-1.5 text-left">
            <ChevronDown className={cn('size-4 text-muted-foreground transition-transform', open && 'rotate-180')} aria-hidden />{title}
          </button>
          {hint && <InfoTip>{hint}</InfoTip>}
        </h3>
        {open && <div id={id} className="space-y-3">{children}</div>}
      </section>
    )
  }
  return (
    <section className="space-y-3" aria-label={title}>
      <h3 className="heading-display flex items-center gap-1.5 border-b pb-1 text-[1.15rem]"><InfoTip of={title}>{hint}</InfoTip></h3>
      {children}
    </section>
  )
}

export function SectionTitle({ children, hint, right }: { children: ReactNode; hint?: ReactNode; right?: ReactNode }) {
  return (
    <div className="mb-3 flex items-center justify-between gap-2">
      <h2 className="flex items-center gap-1.5 text-sm font-semibold">
        <InfoTip of={children}>{hint}</InfoTip>
      </h2>
      {right}
    </div>
  )
}

/** Signed value coloured by direction, with +/− sign so meaning is never colour-only. */
export function Signed({ value, children }: { value: number | null | undefined; children: ReactNode }) {
  const cls = value === null || value === undefined || value === 0 ? '' : value > 0 ? 'text-gain' : 'text-loss'
  return <span className={cn('tabular', cls)}>{children}</span>
}
