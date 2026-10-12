import type { ReactNode } from 'react'
import { AlertTriangle, ArrowDownRight, ArrowUpRight, Database, FlaskConical, Info, Inbox, RefreshCw } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Skeleton } from '@/components/ui/skeleton'
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip'
import { errorMessage } from '@/lib/api'
import type { Metric } from '@/lib/types'
import { cn } from '@/lib/utils'

export function PageHeader({ title, description, actions }: { title: string; description?: ReactNode; actions?: ReactNode }) {
  return (
    <div className="mb-6 flex flex-col gap-3 sm:flex-row sm:items-end sm:justify-between">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
        {description && <p className="mt-1 text-sm text-muted-foreground">{description}</p>}
      </div>
      {actions && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
    </div>
  )
}

/** Small (i) icon with an explanatory tooltip – used for every financial metric. */
export function InfoTip({ children, label = 'More information' }: { children: ReactNode; label?: string }) {
  return (
    <Tooltip>
      <TooltipTrigger
        render={<button type="button" aria-label={label} className="inline-flex text-muted-foreground hover:text-foreground focus-visible:outline-2" />}
      >
        <Info className="size-3.5" />
      </TooltipTrigger>
      <TooltipContent className="max-w-xs text-xs leading-relaxed">{children}</TooltipContent>
    </Tooltip>
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
          {label}
          {hint && <InfoTip label={`About ${label}`}>{hint}</InfoTip>}
        </div>
        {loading ? (
          <Skeleton className="mt-2 h-7 w-28" />
        ) : (
          <div className={cn('mt-1.5 flex items-center gap-1 text-xl font-semibold', tone !== 'neutral' && good > 0 && 'text-gain', tone !== 'neutral' && good < 0 && 'text-loss')}>
            {tone !== 'neutral' && sign > 0 && <ArrowUpRight className="size-4" aria-hidden />}
            {tone !== 'neutral' && sign < 0 && <ArrowDownRight className="size-4" aria-hidden />}
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
        <TooltipTrigger render={<span className="cursor-help text-muted-foreground underline decoration-dotted underline-offset-4" />}>n/a</TooltipTrigger>
        <TooltipContent className="max-w-xs text-xs">{metric.unavailableReason}</TooltipContent>
      </Tooltip>
    )
  }
  return <span>{format(metric.value)}</span>
}

export type DataKind = 'live' | 'cached' | 'stale' | 'simulated' | 'backtest' | 'estimate' | 'missing'

const dataKindStyles: Record<DataKind, { label: string; className: string; icon: ReactNode; title: string }> = {
  live: { label: 'Live data', className: 'border-gain/40 text-gain', icon: <Database className="size-3" />, title: 'Real market data from the provider.' },
  cached: { label: 'Cached', className: 'border-border text-muted-foreground', icon: <Database className="size-3" />, title: 'Stored market data from the last synchronization.' },
  stale: { label: 'Stale', className: 'border-warn/50 text-warn', icon: <AlertTriangle className="size-3" />, title: 'The latest price is older than the staleness threshold.' },
  simulated: { label: 'SIMULATED', className: 'border-series-2/60 bg-series-2/10 text-foreground', icon: <FlaskConical className="size-3" />, title: 'Synthetic prices from the mock provider – not real market data.' },
  backtest: { label: 'Backtest', className: 'border-series-7/50 text-foreground', icon: <RefreshCw className="size-3" />, title: 'Hypothetical historical simulation – not a forecast.' },
  estimate: { label: 'Estimate', className: 'border-border text-muted-foreground', icon: <Info className="size-3" />, title: 'Historical estimate used as model input – not a forecast.' },
  missing: { label: 'No data', className: 'border-loss/40 text-loss', icon: <AlertTriangle className="size-3" />, title: 'No price data stored for this asset.' },
}

export function DataBadge({ kind, label }: { kind: DataKind; label?: string }) {
  const s = dataKindStyles[kind]
  return (
    <Badge variant="outline" title={s.title} className={cn('gap-1 font-medium', s.className)}>
      {s.icon}
      {label ?? s.label}
    </Badge>
  )
}

export function EmptyState({ title, description, action, icon }: { title: string; description?: ReactNode; action?: ReactNode; icon?: ReactNode }) {
  return (
    <div className="flex flex-col items-center justify-center rounded-xl border border-dashed px-6 py-12 text-center">
      <div className="mb-3 text-muted-foreground">{icon ?? <Inbox className="size-8" />}</div>
      <h3 className="font-medium">{title}</h3>
      {description && <p className="mt-1 max-w-md text-sm text-muted-foreground">{description}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  )
}

export function ErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  return (
    <Alert variant="destructive">
      <AlertTriangle />
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

export function Warnings({ items, title = 'Data notes' }: { items: string[] | undefined; title?: string }) {
  if (!items || items.length === 0) return null
  return (
    <Alert className="border-warn/40">
      <AlertTriangle className="text-warn" />
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

export function SectionTitle({ children, hint, right }: { children: ReactNode; hint?: ReactNode; right?: ReactNode }) {
  return (
    <div className="mb-3 flex items-center justify-between gap-2">
      <h2 className="flex items-center gap-1.5 text-sm font-semibold">
        {children}
        {hint && <InfoTip>{hint}</InfoTip>}
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
