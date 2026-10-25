import { useDeferredValue, useState } from 'react'
import { Link, useNavigate } from 'react-router'
import { Loader2, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { EmptyState, ErrorState, InfoTip, LoadingBlock, PageHeader } from '@/components/common'
import { QuoteCell, QuoteProvenance, RefreshQuoteButton } from '@/components/stock-selection'
import { ChangeText, RangePicker, Sparkline } from '@/components/watchlist'
import { errorMessage } from '@/lib/api'
import { date } from '@/lib/format'
import { useAddToWatchlist, useRefreshWatchlist, useRemoveFromWatchlist, useStocks, useWatchlist } from '@/lib/queries'
import type { HistoryRange, WatchlistItem } from '@/lib/types'

const listRanges: HistoryRange[] = ['1M', '3M', '6M', '1Y', '5Y']

export function WatchlistPage() {
  const [range, setRange] = useState<HistoryRange>('1M')
  const list = useWatchlist(range)
  const refresh = useRefreshWatchlist()
  const items = list.data?.items ?? []

  const refreshAll = async () => {
    try {
      const result = await refresh.mutateAsync()
      const updated = result.items.filter((i) => i.outcome === 'Updated').length
      const fresh = result.items.filter((i) => i.outcome === 'Fresh').length
      const failed = result.items.filter((i) => i.outcome === 'Failed' || i.outcome === 'Unsupported')
      toast.success(`Prices: ${updated} updated, ${fresh} already recent${failed.length ? `, ${failed.length} unavailable` : ''}`, {
        description: failed.length ? failed.map((f) => `${f.symbol}: ${f.message ?? f.outcome}`).join(' · ') : undefined,
      })
    } catch (e) {
      toast.error('Refresh failed', { description: errorMessage(e) })
    }
  }

  return (
    <div>
      <PageHeader
        title="Watchlist"
        info="Stocks you follow – latest available price, change over a period, price history and company news. Watching a stock is not a purchase and never changes a portfolio or run."
        actions={
          <>
            <RangePicker value={range} onChange={setRange} ranges={listRanges} label="Change period" />
            <Button variant="outline" size="sm" onClick={refreshAll} disabled={refresh.isPending || items.length === 0}>
              Refresh prices
            </Button>
          </>
        }
      />

      <AddStock />

      {list.error ? <ErrorState error={list.error} onRetry={() => list.refetch()} /> : list.isLoading ? <LoadingBlock /> : items.length === 0 ? (
        <EmptyState
          title="Your watchlist is empty"
          description="Search above by company name or ticker (e.g. “Siemens”, “AAPL”, “7203.T”) and add stocks to follow their prices and news."
        />
      ) : (
        <div className="space-y-2" aria-live="polite">
          {items.map((i) => <WatchRow key={i.id} item={i} range={range} />)}
          <p className="pt-2 text-xs text-muted-foreground">
            {list.data!.note} {items.length} of {list.data!.maxEntries} slots used.
          </p>
        </div>
      )}
    </div>
  )
}

function WatchRow({ item, range }: { item: WatchlistItem; range: HistoryRange }) {
  const s = item.stock
  const remove = useRemoveFromWatchlist()
  const navigate = useNavigate()
  const p = item.period
  const removeNow = async () => {
    try {
      await remove.mutateAsync(s.symbol)
      toast.success(`${s.symbol} removed from your watchlist`)
    } catch (e) {
      toast.error('Could not remove', { description: errorMessage(e) })
    }
  }
  // A click anywhere on the row opens the stock page, except on the row's own controls (symbol link, refresh, remove,
  // info). Keyboard and screen-reader users open it through the symbol link.
  const openDetail = (e: React.MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement
    if (!e.currentTarget.contains(target) || target.closest('a, button, input, [role="button"]')) return
    navigate(`/stocks/${encodeURIComponent(s.symbol)}`)
  }
  return (
    <Card className="cursor-pointer py-3 transition-colors hover:bg-muted/40" onClick={openDetail} data-testid={`watch-row-${s.symbol}`} title={`Open ${s.symbol}`}>
      <CardContent className="grid grid-cols-[1fr_auto] items-center gap-x-4 gap-y-2 px-4 md:grid-cols-[minmax(12rem,1.4fr)_minmax(7rem,auto)_minmax(8rem,auto)_auto_auto]">
        <div className="min-w-0">
          <Link to={`/stocks/${encodeURIComponent(s.symbol)}`} className="font-medium hover:underline">
            {s.symbol}
          </Link>
          <span className="ml-2 truncate text-sm text-muted-foreground">{s.name ?? ''}</span>
          <div className="mt-0.5 flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
            <span>{[s.exchange, s.currency].filter(Boolean).join(' · ')}</span>
            {s.observations === 0 && <Badge variant="outline" className="border-warn/50 text-warn">History downloading or unavailable</Badge>}
          </div>
          <div className="mt-1 hidden md:block"><QuoteProvenance quote={s.quote} /></div>
        </div>

        {/* Phones show the price's kind, date and flags here because the provenance line above is hidden there. */}
        <div className="text-right md:hidden"><QuoteCell quote={s.quote} /></div>
        <div className="hidden text-right md:block"><QuoteCell quote={s.quote} compact /></div>

        <div className="col-span-2 flex items-center justify-between gap-3 md:col-span-1 md:flex-col md:items-end md:gap-0.5">
          <div className="flex items-center gap-1 text-sm">
            <span className="text-xs text-muted-foreground">{range}</span>
            <ChangeText value={p.change} className="font-medium" />
            <InfoTip label={`About the ${range} change`}>
              {p.change !== null
                ? `${p.basis}: ${date(p.baseDate)} → ${date(p.endDate)}. ${p.limitation ?? ''}`
                : p.limitation ?? 'Not enough stored history for this period.'}
            </InfoTip>
          </div>
          <Sparkline points={p.sparkline} label={`${s.symbol} ${p.basis} over ${range}`} />
        </div>

        <div className="hidden md:block" />

        <div className="col-span-2 flex items-center justify-end gap-1 md:col-span-1">
          <RefreshQuoteButton symbol={s.symbol} />
          <Button size="icon-xs" variant="ghost" onClick={removeNow} disabled={remove.isPending} aria-label={`Remove ${s.symbol} from watchlist`} title="Remove">
            {remove.isPending ? <Loader2 className="animate-spin" /> : <Trash2 />}
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

/** Search the catalogue by name or ticker and add a listing; duplicates are rejected by the server (409). */
function AddStock() {
  const [term, setTerm] = useState('')
  const search = useDeferredValue(term.trim())
  const results = useStocks({ search, pageSize: 8, sort: 'name' }, search.length >= 1)
  const watch = useWatchlist('1M')
  const add = useAddToWatchlist()
  const watched = new Set(watch.data?.items.map((i) => i.stock.symbol) ?? [])

  const addNow = async (symbol: string) => {
    try {
      const r = await add.mutateAsync(symbol)
      toast.success(`${symbol} added to your watchlist`, {
        description: r.historyQueued ? 'Its daily price history is being downloaded; charts fill in when it completes.' : undefined,
      })
      setTerm('')
    } catch (e) {
      toast.error(`Could not add ${symbol}`, { description: errorMessage(e) })
    }
  }

  return (
    <div className="relative mb-5">
      <label htmlFor="watch-search" className="sr-only">Search stocks to add</label>
      <Input id="watch-search" value={term} onChange={(e) => setTerm(e.target.value)} placeholder="Add a stock: company name or ticker…" autoComplete="off" />
      {search && (
        <Card className="absolute z-20 mt-1 w-full gap-0 py-1 shadow-lg">
          {results.isLoading ? <div className="p-3 text-sm text-muted-foreground">Searching…</div> :
            results.data?.items.length === 0 ? <div className="p-3 text-sm text-muted-foreground">No stock in the supported markets matches “{search}”.</div> : (
              <ul>
                {results.data?.items.map((s) => (
                  <li key={s.id} className="flex items-center justify-between gap-2 px-3 py-1.5 text-sm">
                    <div className="min-w-0">
                      <span className="font-medium">{s.symbol}</span>
                      <span className="ml-2 text-muted-foreground">{s.name}</span>
                      <span className="ml-2 text-xs text-muted-foreground">{[s.exchange, s.currency].filter(Boolean).join(' · ')}</span>
                    </div>
                    {watched.has(s.symbol) ? (
                      <Badge variant="outline">Watching</Badge>
                    ) : (
                      <Button size="sm" variant="outline" onClick={() => addNow(s.symbol)} disabled={add.isPending}>
                        Add
                      </Button>
                    )}
                  </li>
                ))}
              </ul>
            )}
        </Card>
      )}
    </div>
  )
}
