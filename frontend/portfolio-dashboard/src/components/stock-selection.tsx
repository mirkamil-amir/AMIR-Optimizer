import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Link } from 'react-router'
import { X } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { DataBadge, InfoTip } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useAfterPriceDownload, useHistoryCoverage, useRefreshQuote, useStartPriceDownload, useSyncJobs } from '@/lib/queries'
import { age, quoteStatus } from '@/lib/quote-status'
import { useSelection } from '@/lib/selection'
import type { StockQuote, SyncJob } from '@/lib/types'
import { money, pct } from '@/lib/format'
import { cn } from '@/lib/utils'
import { MatrixLoader, WorkingPanel } from '@/components/matrix-loader'

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
      {status.when && <span>· {status.when}</span>}
      {quote.retrievedAtUtc && <span>· retrieved {age(quote.retrievedAtUtc)}</span>}
      <InfoTip label="Price details">{quote.description}</InfoTip>
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
      Refresh price
    </Button>
  ) : (
    <Button size="xs" variant="ghost" onClick={run} disabled={refresh.isPending} aria-label={label} title={label}>
      {refresh.isPending ? <MatrixLoader /> : 'Refresh'}
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
/** The temporary selection. `actions` replaces the default links into the current portfolio's optimizations. */
export function SelectionTray({ className, actions }: { className?: string; actions?: ReactNode }) {
  const { portfolio } = usePortfolioContext()
  const analysisCurrency = portfolio?.baseCurrency ?? 'USD'
  const selection = useSelection()
  // Coverage of the whole selection – also beyond the first page of a large selection.
  const coverage = useHistoryCoverage(selection.symbols)
  const start = useStartPriceDownload()
  const [jobIds, setJobIds] = useState<string[]>([])
  const download = useDownloadProgress(jobIds)
  const missing = (coverage.data ?? []).filter((s) => s.observations < 61).map((s) => s.symbol)

  if (selection.count === 0) return null

  const fetchMissing = async () => {
    try {
      setJobIds(await start.mutateAsync(missing))
    } catch (e) {
      toast.error('Could not queue the download', { description: errorMessage(e) })
    }
  }

  return (
    <div className={cn('rounded-xl border bg-background p-3 shadow-sm', className)} role="region" aria-label="Selected stocks">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2 text-sm font-medium">
          <InfoTip of="Selection">Your selection is kept in this browser tab only and is not saved. It is stored when you create a portfolio from it, or as part of a run when you start an optimization or ML experiment.</InfoTip>
          <Badge variant="secondary" className="tabular">{selection.count}</Badge>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {missing.length > 0 && !download.running && (
            <Button size="sm" variant="outline" onClick={fetchMissing} disabled={start.isPending || coverage.isFetching}>
              {start.isPending && <MatrixLoader />}
              Fetch prices ({missing.length})
            </Button>
          )}
          <Button size="sm" variant="ghost" onClick={selection.clear}>
            Clear all
          </Button>
          {actions ?? (portfolio && (
            // Both name the target, so the selection is never applied to a portfolio by accident.
            <>
              <Button size="sm" variant="outline" render={<Link to={`/portfolios/${portfolio.id}/optimize?from=selection`} />}
                title={`Use this selection as the universe in the Math Optimization of “${portfolio.name}”`}>
                Math Optimization in “{portfolio.name}”
              </Button>
              <Button size="sm" variant="outline" render={<Link to={`/portfolios/${portfolio.id}/ml-optimize?from=selection`} />}
                title={`Use this selection as the universe in the ML Optimization of “${portfolio.name}”`}>
                ML Optimization in “{portfolio.name}”
              </Button>
            </>
          ))}
        </div>
      </div>
      {selection.currencies.some((c) => c !== analysisCurrency) && (
        <p className="mt-2 flex items-start gap-1.5 text-xs text-muted-foreground" role="status" aria-label="Currency conversion">
          Your selection trades in {selection.currencies.join(', ')}. Analyses for “{portfolio?.name ?? 'your portfolio'}” are computed in {analysisCurrency}:
          prices are converted with the reference rate of each date, so returns include currency movements (unhedged).
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
      {jobIds.length > 0 && <DownloadProgress progress={download} onClose={() => setJobIds([])} />}
      {missing.length > 0 && !download.running && (
        <p className="mt-2 text-xs text-warn">
          {missing.length} selected {missing.length === 1 ? 'stock has' : 'stocks have'} fewer than 61 stored prices and would be excluded from analysis until prices are fetched.
        </p>
      )}
    </div>
  )
}

interface Progress { running: boolean; total: number; done: number; failed: SyncJob['logs']; last: string | null; succeeded: number }

/** Combined progress of the download jobs of one "Fetch prices" (50 stocks per job); refreshes the lists when all are done. */
function useDownloadProgress(ids: string[]): Progress {
  const jobs = useSyncJobs(ids).map((q) => q.data).filter((j): j is SyncJob => !!j)
  const after = useAfterPriceDownload()
  const running = ids.length > 0 && (jobs.length < ids.length || jobs.some((j) => j.status === 'Queued' || j.status === 'Running'))
  const total = jobs.reduce((n, j) => n + j.assetsTotal, 0)
  const succeeded = jobs.reduce((n, j) => n + j.assetsCompleted, 0)
  const done = jobs.reduce((n, j) => n + j.assetsCompleted + j.assetsFailed, 0)
  const failed = jobs.flatMap((j) => j.logs.filter((l) => !l.success))
  const logs = jobs.flatMap((j) => j.logs).sort((a, b) => a.completedAtUtc.localeCompare(b.completedAtUtc))
  const last = logs.at(-1)?.symbol ?? null

  // Once per download: reload coverage and prices, and say how it ended.
  const reported = useRef('')
  const key = ids.join(',')
  useEffect(() => {
    if (!key || running || reported.current === key || jobs.length < ids.length) return
    reported.current = key
    after()
    if (failed.length === 0) toast.success('Price history downloaded', { description: `${succeeded} ${succeeded === 1 ? 'stock' : 'stocks'}.` })
    else toast.warning(`Price history: ${succeeded} downloaded, ${failed.length} failed`, { description: failed.slice(0, 3).map((f) => f.message).join(' · ') })
  }, [key, running, jobs.length, ids.length, after, failed, succeeded])

  return { running, total, done, failed, last, succeeded }
}

/** The download in the selection: running (matrix loader, stocks done, last stock) or finished (summary with failures). */
function DownloadProgress({ progress: p, onClose }: { progress: Progress; onClose: () => void }) {
  if (p.running) {
    return (
      <WorkingPanel className="mt-3" title="Downloading price history…" percent={p.total ? (p.done / p.total) * 100 : null}
        detail={`${p.done} of ${p.total || '…'} stocks${p.last ? ` · last: ${p.last}` : ''}${p.failed.length ? ` · ${p.failed.length} failed` : ''}`} />
    )
  }
  return (
    <div className="mt-3 rounded-xl border bg-card px-4 py-3 text-sm" role="status" aria-label="Download result">
      <div className="flex items-center justify-between gap-3">
        <span className="font-medium">Price history downloaded: {p.succeeded} of {p.total} {p.total === 1 ? 'stock' : 'stocks'}</span>
        <Button size="xs" variant="ghost" onClick={onClose}>Close</Button>
      </div>
      {p.failed.length > 0 && (
        <ul className="mt-1.5 space-y-0.5 text-xs text-warn">
          {p.failed.map((f) => <li key={f.symbol}>{f.message ?? `${f.symbol}: failed`}</li>)}
        </ul>
      )}
    </div>
  )
}
