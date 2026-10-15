import { Eye, EyeOff, Loader2 } from 'lucide-react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { ChartCard, TimeSeriesChart, timeSeriesTable } from '@/components/charts'
import { DataBadge, ErrorState, LoadingBlock } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { date, dateTime, money, pct } from '@/lib/format'
import { useAddToWatchlist, useRemoveFromWatchlist, useStockHistory, useWatchlist } from '@/lib/queries'
import type { HistoryRange, PricePoint } from '@/lib/types'
import { cn } from '@/lib/utils'

const historyRanges: HistoryRange[] = ['1D', '1M', '3M', '6M', '1Y', '5Y']

/** Segmented range picker. 1D is offered so its unavailability is explained rather than silently hidden. */
export function RangePicker({ value, onChange, ranges = historyRanges, label = 'Time range' }: {
  value: HistoryRange
  onChange: (r: HistoryRange) => void
  ranges?: HistoryRange[]
  label?: string
}) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex rounded-md border p-0.5">
      {ranges.map((r) => (
        <button
          key={r}
          type="button"
          role="radio"
          aria-checked={value === r}
          onClick={() => onChange(r)}
          className={cn('rounded px-2.5 py-1 text-xs font-medium tabular text-muted-foreground transition-colors hover:text-foreground',
            value === r && 'bg-muted text-foreground')}
        >
          {r}
        </button>
      ))}
    </div>
  )
}

/** Minimal line of real observations (no interpolation beyond straight segments between stored closes). */
export function Sparkline({ points, label, width = 112, height = 32 }: { points: PricePoint[]; label: string; width?: number; height?: number }) {
  if (points.length < 2) return <span className="text-xs text-muted-foreground">—</span>
  const prices = points.map((p) => p.price)
  const min = Math.min(...prices)
  const max = Math.max(...prices)
  const span = max - min || 1
  const path = points
    .map((p, i) => `${(i / (points.length - 1)) * (width - 2) + 1},${height - 1 - ((p.price - min) / span) * (height - 2)}`)
    .join(' ')
  const up = prices[prices.length - 1] >= prices[0]
  return (
    <svg width={width} height={height} viewBox={`0 0 ${width} ${height}`} role="img" aria-label={label} className="overflow-visible">
      <polyline points={path} fill="none" strokeWidth={1.5} className={up ? 'stroke-gain' : 'stroke-loss'} />
    </svg>
  )
}

/** Signed percentage with arrow and sign, so direction is never conveyed by colour alone. */
export function ChangeText({ value, className }: { value: number | null; className?: string }) {
  if (value === null) return <span className={cn('text-muted-foreground', className)}>n/a</span>
  const up = value > 0
  const down = value < 0
  return (
    <span className={cn('tabular', up && 'text-gain', down && 'text-loss', className)}>
      {up ? '▲ ' : down ? '▼ ' : ''}{pct(value, 2, true)}
    </span>
  )
}

/** Adds or removes a stock from the caller's watchlist (never touches portfolios or the temporary selection). */
export function WatchToggle({ symbol, size = 'sm' }: { symbol: string; size?: 'sm' | 'icon-xs' }) {
  const list = useWatchlist('1M')
  const add = useAddToWatchlist()
  const remove = useRemoveFromWatchlist()
  const watched = list.data?.items.some((i) => i.stock.symbol === symbol) ?? false
  const busy = add.isPending || remove.isPending || list.isLoading
  const run = async (e: React.MouseEvent) => {
    e.stopPropagation()
    try {
      if (watched) {
        await remove.mutateAsync(symbol)
        toast.success(`${symbol} removed from your watchlist`)
      } else {
        const result = await add.mutateAsync(symbol)
        toast.success(`${symbol} added to your watchlist`, {
          description: result.historyQueued ? 'Its daily price history is being downloaded for the charts.' : undefined,
        })
      }
    } catch (err) {
      toast.error(`Watchlist: ${symbol}`, { description: errorMessage(err) })
    }
  }
  const label = watched ? `Remove ${symbol} from watchlist` : `Add ${symbol} to watchlist`
  if (size === 'icon-xs') {
    return (
      <Button size="icon-xs" variant="ghost" onClick={run} disabled={busy} aria-label={label} title={label} aria-pressed={watched}>
        {busy ? <Loader2 className="animate-spin" /> : watched ? <EyeOff /> : <Eye />}
      </Button>
    )
  }
  return (
    <Button size="sm" variant="outline" onClick={run} disabled={busy} aria-pressed={watched}>
      {busy ? <Loader2 className="animate-spin" /> : watched ? <EyeOff /> : <Eye />}
      {watched ? 'Unwatch' : 'Watch'}
    </Button>
  )
}

/** Price chart for a range, with basis, currency, source, the period change and honest gaps. */
export function PriceHistoryCard({ symbol, range, onRangeChange }: { symbol: string; range: HistoryRange; onRangeChange: (r: HistoryRange) => void }) {
  const history = useStockHistory(symbol, range)
  const h = history.data
  const dates = h?.points.map((p) => p.date) ?? []
  const series = h ? [{ name: `${h.basis} (${h.currency})`, values: h.points.map((p) => p.price) }] : []
  return (
    <ChartCard
      title="Price history"
      hint="Daily closes stored from the configured provider. Adjusted closes include splits and dividends (total return); raw closes are the traded prices. Missing days are not filled in."
      description={h ? (
        <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span>{h.basis} · {h.currency} · {h.exchange} · source {h.source}</span>
          {h.isSimulated && <DataBadge kind="simulated" />}
          {h.lastRetrievedAtUtc && <span>· retrieved {dateTime(h.lastRetrievedAtUtc)}</span>}
        </span>
      ) : undefined}
      table={h && h.points.length > 0 ? timeSeriesTable(dates, series, (v) => money(v, h.currency)) : undefined}
    >
      <div className="mb-3"><RangePicker value={range} onChange={onRangeChange} /></div>
      {history.error ? <ErrorState error={history.error} onRetry={() => history.refetch()} /> : !h ? <LoadingBlock height="h-56" /> : (
        <div className="space-y-3">
          {h.points.length > 1 ? (
            <>
              <div className="flex flex-wrap items-baseline gap-x-3 gap-y-1 text-sm">
                <span className="text-muted-foreground">Change {range}:</span>
                <ChangeText value={h.change} className="font-semibold" />
                <span className="text-xs text-muted-foreground">
                  {money(h.basePrice, h.currency)} on {date(h.baseDate)} → {money(h.endPrice, h.currency)} on {date(h.endDate)}
                </span>
              </div>
              <TimeSeriesChart dates={dates} series={series} format={(v) => money(v, h.currency, { compact: true })} height={240} />
            </>
          ) : (
            <p className="rounded-md border border-dashed p-4 text-sm text-muted-foreground">
              {h.limitation ?? 'No prices for this range.'}
              {h.availableFrom && range !== '1D' && <> Stored history begins {date(h.availableFrom)}.</>}
            </p>
          )}
          {h.points.length > 1 && h.limitation && <p className="text-xs text-muted-foreground">{h.limitation}</p>}
        </div>
      )}
    </ChartCard>
  )
}
