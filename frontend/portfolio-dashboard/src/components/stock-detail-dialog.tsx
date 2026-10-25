import { useEffect, useState } from 'react'
import { Link } from 'react-router'
import { Loader2 } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogTitle } from '@/components/ui/dialog'
import { Skeleton } from '@/components/ui/skeleton'
import { ErrorState } from '@/components/common'
import { CompanyOverview } from '@/components/company-overview'
import { QuoteProvenance, RefreshQuoteButton } from '@/components/stock-selection'
import { ChangeText, PriceHistoryCard, WatchToggle } from '@/components/watchlist'
import { errorMessage } from '@/lib/api'
import { date, dateTime, money, num } from '@/lib/format'
import { useFetchPrices, useStock } from '@/lib/queries'
import { useSelection } from '@/lib/selection'
import { stockPosition } from '@/lib/stock-navigation'
import type { HistoryRange, Market, StockListItem } from '@/lib/types'

/** Chart height follows the viewport so the chart fills the panel on large screens and stays usable on small ones. */
function useChartHeight() {
  const compute = () => (typeof window === 'undefined' ? 360 : Math.round(Math.min(520, Math.max(240, window.innerHeight * 0.42))))
  const [height, setHeight] = useState(compute)
  useEffect(() => {
    const onResize = () => setHeight(compute())
    window.addEventListener('resize', onResize)
    return () => window.removeEventListener('resize', onResize)
  }, [])
  return height
}

/**
 * Large stock-detail panel opened from the stock list (about two thirds of the screen on desktop, full screen on
 * phones). Previous/Next follow the list exactly as displayed and keep the panel open; Close, Escape or a click outside
 * return to the unchanged list and selection. Every section is keyed by the symbol, so data of different stocks never mixes.
 */
export function StockDetailDialog({ symbol, items, markets, onNavigate, onClose }: {
  symbol: string | null
  /** The list in its displayed order (filters, sort and page applied). */
  items: readonly StockListItem[]
  markets: Market[] | undefined
  onNavigate: (symbol: string) => void
  onClose: () => void
}) {
  const position = stockPosition(items.map((i) => i.symbol), symbol)
  // The chosen chart range stays when moving between stocks.
  const [range, setRange] = useState<HistoryRange>('1Y')
  const go = (target: string | null) => { if (target) onNavigate(target) }

  return (
    <Dialog open={symbol !== null} onOpenChange={(open) => { if (!open) onClose() }}>
      <DialogContent
        showCloseButton={false}
        aria-describedby={undefined}
        className="flex h-[100dvh] w-screen max-w-none! flex-col gap-0 overflow-hidden rounded-none p-0 sm:max-w-none! md:h-[min(92vh,960px)] md:w-[94vw] md:rounded-xl lg:h-[min(88vh,960px)] lg:w-[min(68vw,1280px)]"
        onKeyDown={(e) => {
          // ←/→ switch stocks unless the user is typing or using a control that needs the arrow keys.
          const t = e.target as HTMLElement
          if (t.closest('input, textarea, select, [role="radiogroup"], [role="slider"]')) return
          if (e.key === 'ArrowLeft') { e.preventDefault(); go(position.previous) }
          if (e.key === 'ArrowRight') { e.preventDefault(); go(position.next) }
        }}
      >
        {symbol && (
          <StockDetailBody
            key={symbol /* remount per stock: no state or pending UI from the previous stock survives */}
            symbol={symbol}
            listItem={items.find((i) => i.symbol === symbol)}
            markets={markets}
            position={position}
            range={range}
            onRangeChange={setRange}
            onPrevious={() => go(position.previous)}
            onNext={() => go(position.next)}
            onClose={onClose}
          />
        )}
      </DialogContent>
    </Dialog>
  )
}

function StockDetailBody({ symbol, listItem, markets, position, range, onRangeChange, onPrevious, onNext, onClose }: {
  symbol: string
  listItem: StockListItem | undefined
  markets: Market[] | undefined
  position: ReturnType<typeof stockPosition>
  range: HistoryRange
  onRangeChange: (r: HistoryRange) => void
  onPrevious: () => void
  onNext: () => void
  onClose: () => void
}) {
  const detail = useStock(symbol)
  const selection = useSelection()
  const fetchPrices = useFetchPrices()
  const chartHeight = useChartHeight()
  // The list row is already loaded and belongs to this symbol; the detail request adds ISIN, MIC and coverage.
  const s = detail.data?.stock.symbol === symbol ? detail.data.stock : listItem
  const d = detail.data?.stock.symbol === symbol ? detail.data : undefined

  const fetchHistory = async () => {
    try {
      const job = await fetchPrices.mutateAsync([symbol])
      if (job.assetsFailed === 0) toast.success(`Price history for ${symbol} downloaded`)
      else toast.error(`Price history for ${symbol} could not be downloaded`, { description: job.message ?? undefined })
    } catch (e) {
      toast.error('Could not request the price history', { description: errorMessage(e) })
    }
  }

  return (
    <>
      <header className="border-b px-4 py-3 md:px-6 md:py-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <DialogTitle className="truncate text-lg font-semibold md:text-xl">
              {s?.name ?? symbol} <span className="font-normal text-muted-foreground">· {symbol}</span>
            </DialogTitle>
            <DialogDescription className="text-xs md:text-sm">
              {s ? [s.exchange, s.currency, s.sector ?? 'Unclassified', s.industry].filter(Boolean).join(' · ') : 'Loading…'}
            </DialogDescription>
          </div>
          <nav aria-label="Stock navigation" className="flex items-center gap-1.5">
            <span className="mr-1 hidden text-xs text-muted-foreground tabular sm:inline" aria-live="polite">
              {position.index >= 0 ? `${position.index + 1} of ${position.total} in the current list` : 'No longer in the current list'}
            </span>
            <Button variant="outline" size="sm" onClick={onPrevious} disabled={!position.previous}
              title={position.previous ? `Previous: ${position.previous} (←)` : 'This is the first stock in the list'}>
              Previous
            </Button>
            <Button variant="outline" size="sm" onClick={onNext} disabled={!position.next}
              title={position.next ? `Next: ${position.next} (→)` : 'This is the last stock in the list'}>
              Next
            </Button>
            <Button variant="ghost" size="sm" onClick={onClose} title="Close (Esc)">
              Close
            </Button>
          </nav>
        </div>
        {s && (
          <div className="mt-3 flex flex-wrap items-end justify-between gap-3">
            <div>
              <div className="flex flex-wrap items-baseline gap-x-3">
                <span className="text-2xl font-semibold tabular md:text-3xl">{s.quote.price !== null ? money(s.quote.price, s.quote.currency) : 'No price available'}</span>
                {s.quote.changePercent !== null && (
                  <span className="text-sm">
                    <ChangeText value={s.quote.changePercent} />{' '}
                    <span className="text-muted-foreground">vs previous close{s.quote.previousCloseDate ? ` (${date(s.quote.previousCloseDate)})` : ''}</span>
                  </span>
                )}
              </div>
              <div className="mt-1"><QuoteProvenance quote={s.quote} /></div>
            </div>
            <div className="flex flex-wrap gap-2">
              <RefreshQuoteButton symbol={symbol} size="sm" />
              <WatchToggle symbol={symbol} />
              <Button size="sm" variant={selection.has(symbol) ? 'outline' : 'default'}
                onClick={() => selection.toggle({ symbol, name: s.name, sector: s.sector, currency: s.currency })}>
                {selection.has(symbol) ? 'Remove from selection' : 'Add to selection'}
              </Button>
            </div>
          </div>
        )}
      </header>

      {/* One scroll area for the whole panel body (no nested scrolling). */}
      <div className="min-h-0 flex-1 overflow-y-auto px-4 py-4 md:px-6 md:py-5">
        {detail.error && !listItem ? <ErrorState error={detail.error} onRetry={() => detail.refetch()} /> : (
          <div className="grid items-start gap-5 xl:grid-cols-[minmax(0,1fr)_18rem]">
            {/* Chart first, the company overview directly below it; key facts alongside on wide screens. */}
            <div className="min-w-0 space-y-5">
              {s && s.observations === 0 ? (
                <div className="flex flex-col items-start gap-3 rounded-xl border border-dashed p-6 text-sm">
                  <p><strong>No daily prices are stored for {symbol} yet.</strong> The chart needs its history from the configured provider.</p>
                  <Button variant="outline" size="sm" onClick={fetchHistory} disabled={fetchPrices.isPending}>
                    {fetchPrices.isPending && <Loader2 className="animate-spin" />} Download price history
                  </Button>
                </div>
              ) : (
                <PriceHistoryCard symbol={symbol} range={range} onRangeChange={onRangeChange} height={chartHeight} />
              )}
              <CompanyOverview symbol={symbol} />
            </div>
            <KeyFacts symbol={symbol} s={s} d={d} markets={markets} />
          </div>
        )}
      </div>
    </>
  )
}

function KeyFacts({ symbol, s, d, markets }: {
  symbol: string
  s: StockListItem | undefined
  d: ReturnType<typeof useStock>['data']
  markets: Market[] | undefined
}) {
  if (!s) return <Skeleton className="h-64 w-full rounded-xl" />
  const indexName = (code: string) => markets?.flatMap((m) => m.indices).find((i) => i.code === code)?.name ?? code
  return (
    <aside aria-label="Key facts" className="space-y-3 rounded-xl border p-4 text-sm">
      <dl className="grid grid-cols-2 gap-x-4 gap-y-2 xl:grid-cols-1">
        <Fact label="Price applies to" value={s.quote.asOfUtc ? dateTime(s.quote.asOfUtc) : date(s.quote.priceDate)} />
        <Fact label="Retrieved" value={s.quote.retrievedAtUtc ? dateTime(s.quote.retrievedAtUtc) : '—'} />
        <Fact label="Currency" value={s.currency} />
        <Fact label="Listing (MIC)" value={[s.exchange, d?.mic].filter(Boolean).join(' · ') || '—'} />
        <Fact label="Sector · industry" value={[s.sector ?? 'Unclassified', s.industry].filter(Boolean).join(' · ')} />
        <Fact label="Index membership" value={s.indices.length ? s.indices.map(indexName).join(', ') : '—'} />
        <Fact label="ISIN" value={d ? d.isin ?? 'Not provided by the source' : '…'} />
        <Fact label="Stored history" value={s.observations > 0 ? `${num(s.observations, 0)} days (${date(s.firstDate)} – ${date(s.lastDate)})` : 'None yet'} />
        {d && s.observations > 0 && <Fact label="Price basis" value={d.hasAdjustedCloses ? 'Adjusted closes (splits and dividends)' : 'Raw closes only'} />}
      </dl>
      {s.observations > 0 && s.observations < 61 && (
        <Badge variant="outline" className="border-warn/50 text-warn">Fewer than 61 daily prices – excluded from analyses</Badge>
      )}
      {d?.metadataSource && <p className="text-xs text-muted-foreground">Classification source: {d.metadataSource}.</p>}
      <Button size="sm" variant="ghost" className="-ml-2" render={<Link to={`/stocks/${encodeURIComponent(symbol)}`} />}>
        Open full page 
      </Button>
    </aside>
  )
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="tabular">{value}</dd>
    </div>
  )
}
