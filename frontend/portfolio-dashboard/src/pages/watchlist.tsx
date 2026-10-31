import { useDeferredValue, useState, type ReactNode } from 'react'
import { Link, useNavigate } from 'react-router'
import { Trash2 } from 'lucide-react'
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
import { useAddToWatchlist, useMarkets, useRefreshWatchlist, useRemoveFromWatchlist, useReorderWatchlist, useStocks, useWatchlist } from '@/lib/queries'
import type { HistoryRange, WatchlistItem } from '@/lib/types'
import { MatrixLoader } from '@/components/matrix-loader'
import { RangePager } from '@/components/data-table'
import { SortableList } from '@/components/sortable-list'
import { moveItem } from '@/lib/reorder'
import { indexName, sectorAndIndices } from '@/lib/markets'
import { fitColumns } from '@/lib/column-fit'
import { useElementWidth } from '@/lib/use-element-width'
import { cn } from '@/lib/utils'

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
        <WatchList items={items} range={range} note={`${list.data!.note} ${items.length} of ${list.data!.maxEntries} slots used.`} />
      )}
    </div>
  )
}

/**
 * The watched stocks in the user's own order: drag a row by its handle (or use the arrow keys on it) to move it, the
 * order is saved for the account. The list scrolls inside its box and is split into pages (25 rows by default).
 */
function WatchList({ items, range, note }: { items: WatchlistItem[]; range: HistoryRange; note: string }) {
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState<number>(25)
  const [box, setBox] = useState<HTMLDivElement | null>(null)
  const reorder = useReorderWatchlist()
  const totalPages = Math.max(1, Math.ceil(items.length / pageSize))
  const current = Math.min(page, totalPages)
  const offset = (current - 1) * pageSize
  const pageItems = items.slice(offset, offset + pageSize)

  // The rows share one set of columns (CSS subgrid), so they line up and each column is as wide as its longest content
  // between a minimum and a maximum; the list shows as many columns as fit its measured width, by priority, and stacks
  // the rows (phone layout) when even the essentials do not fit.
  const [measure, width] = useElementWidth()
  const fit = fitColumns(WATCH_COLUMNS, width)
  const stacked = width < CORE_WIDTH
  const layout: WatchLayout = { stacked, change: stacked || fit.visible.has('change'), membership: !stacked && fit.visible.has('membership') }
  const template = ['auto', 'minmax(13rem, 1fr)', 'max-content', ...(layout.change ? ['minmax(9rem, 16rem)'] : []), ...(layout.membership ? ['minmax(8rem, 14rem)'] : []), 'max-content'].join(' ')

  const goTo = (p: number) => { setPage(p); box?.scrollTo({ top: 0 }) }
  const move = async (from: number, to: number) => {
    const order = moveItem(items.map((i) => i.stock.symbol), offset + from, offset + to)
    try {
      await reorder.mutateAsync(order)
    } catch (e) {
      toast.error('Could not save the new order', { description: errorMessage(e) })
    }
  }

  return (
    <div className="space-y-3">
      <div ref={(el) => { setBox(el); measure(el) }} className="max-h-[calc(100dvh-15rem)] min-h-48 overflow-y-auto rounded-xl pr-1" tabIndex={-1}>
        <SortableList
          items={pageItems}
          getKey={(i) => i.id}
          getLabel={(i) => i.stock.symbol}
          onMove={move}
          scrollBox={box}
          listClassName={layout.stacked ? undefined : 'grid gap-x-4 gap-y-2'}
          listStyle={layout.stacked ? undefined : { gridTemplateColumns: template }}
          itemClassName={layout.stacked ? undefined : 'col-span-full grid grid-cols-subgrid'}
          renderItem={(i, handle) => <WatchRow item={i} range={range} handle={handle} layout={layout} />}
        />
      </div>
      <RangePager page={current} pageSize={pageSize} total={items.length} totalPages={totalPages} onPage={goTo}
        onPageSize={(n) => { setPageSize(n); goTo(1) }} />
      <p className="text-xs text-muted-foreground">
        Drag a row by its handle to change the order (saved for your account); on a page you can also move a focused handle with the arrow keys. {note}
      </p>
    </div>
  )
}

function WatchRow({ item, range, handle, layout }: { item: WatchlistItem; range: HistoryRange; handle: ReactNode; layout: WatchLayout }) {
  const s = item.stock
  const remove = useRemoveFromWatchlist()
  const navigate = useNavigate()
  const p = item.period
  const markets = useMarkets()
  const membership = sectorAndIndices(s.sector, s.indices, markets.data)
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
  const { stacked } = layout

  const change = (
    <div className={stacked ? 'col-span-3 flex items-center justify-between gap-3' : 'flex min-w-0 flex-col items-end gap-0.5'}>
      <div className="flex items-center gap-1 text-sm">
        <span className="text-xs text-muted-foreground">{range}</span>
        <ChangeText value={p.change} className="font-medium" />
        <InfoTip label={`About the ${range} change`}>
          {p.change !== null
            ? `${p.basis}: ${date(p.baseDate)} → ${date(p.endDate)}. ${p.limitation ?? ''}`
            : p.limitation ?? 'Not enough stored history for this period.'}
        </InfoTip>
      </div>
      {stacked
        ? <Sparkline points={p.sparkline} label={`${s.symbol} ${p.basis} over ${range}`} />
        : <div className="w-full"><Sparkline points={p.sparkline} label={`${s.symbol} ${p.basis} over ${range}`} width={160} height={32} fluid /></div>}
    </div>
  )

  return (
    <Card className={cn('cursor-pointer py-3 transition-colors hover:bg-muted/40', !stacked && 'col-span-full grid grid-cols-subgrid gap-y-0')}
      onClick={openDetail} data-testid={`watch-row-${s.symbol}`} title={`Open ${s.symbol}`}>
      <CardContent className={stacked
        ? 'grid grid-cols-[auto_1fr_auto] items-center gap-x-3 gap-y-2 px-3'
        : 'col-span-full grid grid-cols-subgrid items-center px-3'}>
        {handle}
        <div className="min-w-0">
          {/* Symbol and name on one line; a long name is cut with "…" instead of running into the price. */}
          <div className="flex min-w-0 items-baseline gap-2">
            <Link to={`/stocks/${encodeURIComponent(s.symbol)}`} className="shrink-0 font-medium hover:underline">
              {s.symbol}
            </Link>
            <span className="min-w-0 truncate text-sm text-muted-foreground" title={s.name ?? undefined}>{s.name ?? ''}</span>
          </div>
          <div className="mt-0.5 flex flex-wrap items-center gap-1.5 text-xs text-muted-foreground">
            <span>{[s.exchange, s.currency].filter(Boolean).join(' · ')}</span>
            {/* Sector and indices sit here whenever their own column does not fit. */}
            {!layout.membership && <span className="basis-full">{membership}</span>}
            {s.observations === 0 && <Badge variant="outline" className="border-warn/50 text-warn">History downloading or unavailable</Badge>}
          </div>
          {!stacked && <div className="mt-1"><QuoteProvenance quote={s.quote} /></div>}
        </div>

        {/* Stacked rows show the price's kind, date and flags here because the provenance line is left out there. */}
        <div className="text-right">{stacked ? <QuoteCell quote={s.quote} /> : <QuoteCell quote={s.quote} compact />}</div>

        {(stacked || layout.change) && change}

        {/* Sector and index membership, e.g. "Industrials" over "DAX 40" (index names from the catalogue). */}
        {!stacked && layout.membership && (
          <div className="min-w-0 text-xs leading-snug">
            <div className="text-foreground">{s.sector ?? 'Unclassified'}</div>
            <div className="text-muted-foreground">{s.indices.length ? s.indices.map((i) => indexName(markets.data, i)).join(', ') : 'No tracked index'}</div>
          </div>
        )}

        <div className={cn('flex items-center justify-end gap-1', stacked && 'col-span-3')}>
          <RefreshQuoteButton symbol={s.symbol} />
          <Button size="icon-xs" variant="ghost" onClick={removeNow} disabled={remove.isPending} aria-label={`Remove ${s.symbol} from watchlist`} title="Remove">
            {remove.isPending ? <MatrixLoader /> : <Trash2 />}
          </Button>
        </div>
      </CardContent>
    </Card>
  )
}

interface WatchLayout {
  /** Rows stacked (phone layout) instead of sharing columns. */
  stacked: boolean
  change: boolean
  membership: boolean
}

/** Columns of the watchlist by priority, with their smallest useful widths (gap included) – see lib/column-fit.ts. */
const WATCH_COLUMNS = [
  { key: 'handle', priority: 1, minWidth: 48 },
  { key: 'info', priority: 1, minWidth: 224 },
  { key: 'price', priority: 1, minWidth: 136 },
  { key: 'actions', priority: 1, minWidth: 120 },
  { key: 'change', priority: 2, minWidth: 176 },
  { key: 'membership', priority: 3, minWidth: 160 },
]
/** Below this width even the essential columns do not fit side by side: the rows stack. */
const CORE_WIDTH = WATCH_COLUMNS.filter((c) => c.priority === 1).reduce((sum, c) => sum + c.minWidth, 0)

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
