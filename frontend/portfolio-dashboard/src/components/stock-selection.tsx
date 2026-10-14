import { Link } from 'react-router'
import { AlertTriangle, BrainCircuit, Download, Loader2, RefreshCw, Sigma, Trash2, X } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DataBadge, InfoTip } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { useFetchPrices, useRefreshQuote, useStocks } from '@/lib/queries'
import { age, quoteStatus } from '@/lib/quote-status'
import { useSelection } from '@/lib/selection'
import type { StockQuote } from '@/lib/types'
import { money, pct } from '@/lib/format'
import { cn } from '@/lib/utils'

/** Price with its provenance: what kind of price it is, when it applies, and honest qualifiers. Never labelled "live". */
export function QuoteCell({ quote, compact = false }: { quote: StockQuote; compact?: boolean }) {
  const status = quoteStatus(quote)
  if (status.label === 'No price') {
    return <span className="text-xs text-muted-foreground" title={quote.description}>No price available</span>
  }
  const up = (quote.change ?? 0) > 0
  const down = (quote.change ?? 0) < 0
  return (
    <div className="flex flex-col items-end gap-0.5" title={quote.description}>
      <span className="font-medium tabular">{money(quote.price, quote.currency)}</span>
      {quote.changePercent !== null && (
        <span className={cn('text-xs tabular', up && 'text-gain', down && 'text-loss')}>
          {/* Arrow + sign: direction is never conveyed by colour alone. */}
          {up ? '▲ ' : down ? '▼ ' : ''}{pct(quote.changePercent, 2, true)}
        </span>
      )}
      {!compact && (
        <span className="flex flex-wrap justify-end gap-x-1 text-[10px] text-muted-foreground">
          <span>{status.label}{status.when && ` · ${status.when}`}</span>
          {status.flags.includes('simulated') && <span className="font-semibold text-foreground">· SIMULATED</span>}
          {status.flags.includes('stale') && <span className="text-warn">· stale</span>}
          {status.flags.includes('cached') && !status.flags.includes('stale') && <span>· cached</span>}
        </span>
      )}
    </div>
  )
}

export function QuoteProvenance({ quote }: { quote: StockQuote }) {
  const status = quoteStatus(quote)
  return (
    <div className="flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
      {quote.isSimulated ? <DataBadge kind="simulated" /> : status.label === 'No price' ? <DataBadge kind="missing" /> : quote.isStale ? <DataBadge kind="stale" /> : <DataBadge kind="cached" label={status.label} />}
      <span>{quote.provider ?? '—'}</span>
      {status.when && <span>· {status.when}</span>}
      {quote.retrievedAtUtc && <span>· retrieved {age(quote.retrievedAtUtc)}</span>}
      <InfoTip label="Price source">{quote.description}</InfoTip>
    </div>
  )
}

/**
 * Refreshes one stock's quote through the backend. Disabled while running (no duplicate requests); the server also
 * applies a short cooldown and the provider's rate limits.
 */
export function RefreshQuoteButton({ symbol, size = 'icon-xs' }: { symbol: string; size?: 'icon-xs' | 'sm' }) {
  const refresh = useRefreshQuote()
  const run = async (e: React.MouseEvent) => {
    e.stopPropagation()
    try {
      const result = await refresh.mutateAsync(symbol)
      const item = result.items[0]
      if (item?.outcome === 'Updated') toast.success(`${symbol}: price updated`, { description: describeQuote(result.stocks[0]?.quote) })
      else if (item?.outcome === 'Fresh') toast.info(`${symbol}: price is already current`, { description: item.message ?? undefined })
      else toast.error(`${symbol}: price could not be refreshed`, { description: item?.message ?? 'The data provider returned no price.' })
    } catch (err) {
      toast.error(`${symbol}: refresh failed`, { description: errorMessage(err) })
    }
  }
  const label = `Refresh price of ${symbol}`
  return size === 'sm' ? (
    <Button size="sm" variant="outline" onClick={run} disabled={refresh.isPending} aria-label={label}>
      <RefreshCw className={cn(refresh.isPending && 'animate-spin')} /> Refresh price
    </Button>
  ) : (
    <Button size="icon-xs" variant="ghost" onClick={run} disabled={refresh.isPending} aria-label={label} title={label}>
      <RefreshCw className={cn(refresh.isPending && 'animate-spin')} />
    </Button>
  )
}

function describeQuote(quote?: StockQuote) {
  if (!quote || quote.price === null) return undefined
  const status = quoteStatus(quote)
  return `${money(quote.price, quote.currency)} · ${status.label}${status.when ? ` ${status.when}` : ''}`
}

/**
 * Review of the temporary selection (memory only) with actions to fetch missing prices and continue to the
 * mathematical optimizer or the ML workspace. Nothing is stored until a run is submitted.
 */
export function SelectionTray({ className }: { className?: string }) {
  const selection = useSelection()
  const coverage = useStocks({ symbols: selection.symbols, pageSize: 200 }, selection.count > 0)
  const fetchPrices = useFetchPrices()
  const missing = (coverage.data?.items ?? []).filter((s) => s.observations < 61).map((s) => s.symbol)

  if (selection.count === 0) return null

  const fetchMissing = async () => {
    try {
      const job = await fetchPrices.mutateAsync(missing.slice(0, 50))
      if (job.assetsFailed === 0) toast.success('Price history downloaded', { description: `${job.assetsCompleted} stock(s) from ${job.source}.` })
      else toast.warning(`Price history: ${job.assetsCompleted} downloaded, ${job.assetsFailed} failed`, { description: job.message ?? undefined })
    } catch (e) {
      toast.error('Could not queue the download', { description: errorMessage(e) })
    }
  }

  return (
    <div className={cn('rounded-xl border bg-background p-3 shadow-sm', className)} role="region" aria-label="Selected stocks">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-medium">
          Selection <Badge variant="secondary" className="tabular">{selection.count}</Badge>
          <InfoTip>Your selection is kept in this browser tab only and is not saved. It is stored as part of a run when you start an optimization or ML experiment.</InfoTip>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {missing.length > 0 && (
            <Button size="sm" variant="outline" onClick={fetchMissing} disabled={fetchPrices.isPending}>
              {fetchPrices.isPending ? <Loader2 className="animate-spin" /> : <Download />}
              {fetchPrices.isPending ? 'Downloading history…' : `Fetch prices (${Math.min(missing.length, 50)})`}
            </Button>
          )}
          <Button size="sm" variant="ghost" onClick={selection.clear}>
            <Trash2 /> Clear all
          </Button>
          <Button size="sm" variant="outline" render={<Link to="/optimize?from=selection" />}>
            <Sigma /> Optimize
          </Button>
          <Button size="sm" render={<Link to="/machine-learning?from=selection" />}>
            <BrainCircuit /> ML analysis
          </Button>
        </div>
      </div>
      {selection.currencies.length > 1 && (
        <p className="mt-2 flex items-start gap-1.5 text-xs text-warn" role="status">
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0" aria-hidden />
          Your selection mixes currencies ({selection.currencies.join(', ')}). Optimization, ML and comparisons work on returns in each
          stock&apos;s own currency without FX conversion, so each analysis needs stocks traded in one currency. Remove the others or analyse
          each market separately.
        </p>
      )}
      <ul className="mt-2 flex max-h-28 flex-wrap gap-1.5 overflow-y-auto" aria-label="Selected tickers">
        {selection.stocks.map((s) => {
          const noData = missing.includes(s.symbol)
          return (
            <li key={s.symbol}>
              <Badge variant="outline" className={cn('gap-1 pr-0.5', noData && 'border-warn/50')} title={`${s.name ?? s.symbol}${s.sector ? ` · ${s.sector}` : ''}${noData ? ' · insufficient price history' : ''}`}>
                <span className="font-medium">{s.symbol}</span>
                {noData && <span className="text-[10px] text-warn">no data</span>}
                <button type="button" className="rounded p-0.5 hover:bg-muted" aria-label={`Remove ${s.symbol}`} onClick={() => selection.remove(s.symbol)}>
                  <X className="size-3" />
                </button>
              </Badge>
            </li>
          )
        })}
      </ul>
      {missing.length > 0 && (
        <p className="mt-2 text-xs text-warn">
          {missing.length} selected stock(s) have fewer than 61 stored prices and would be excluded from analysis until prices are fetched.
          {missing.length > 50 && ' At most 50 symbols can be fetched per request.'}
        </p>
      )}
    </div>
  )
}
