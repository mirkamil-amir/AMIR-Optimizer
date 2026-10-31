import { useDeferredValue, useEffect, useRef, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { X } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { StockDetailDialog } from '@/components/stock-detail-dialog'
import { Switch } from '@/components/ui/switch'
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { ConvertedPriceCell, CurrencySelect } from '@/components/currency'
import { DataTable, RangePager, type Column } from '@/components/data-table'
import { EmptyState, ErrorState, InfoTip, LoadingBlock } from '@/components/common'
import { QuoteCell, SelectionTray } from '@/components/stock-selection'
import { ChangeText, Sparkline } from '@/components/watchlist'
import { UniverseNav, type UniverseScope } from '@/components/universe-nav'
import { errorMessage } from '@/lib/api'
import {
  useFxBoard, useFxInfo, useMarkets, useStockTrends, useQuoteRefreshCycle, useRefreshQuotes, useSectors, useStocks, type StockSort,
} from '@/lib/queries'
import { age, symbolsNeedingRefresh } from '@/lib/quote-status'
import { useSelection } from '@/lib/selection'
import type { Market, QuoteRefreshRun, StockListItem, StockTrend } from '@/lib/types'
import { date, num } from '@/lib/format'
import { countryOfSymbol } from '@/lib/markets'
import { useMapSignal } from '@/lib/world-map'
import { MatrixLoader } from '@/components/matrix-loader'
import { cn } from '@/lib/utils'


const sortOptions: { value: StockSort; label: string }[] = [
  { value: 'name', label: 'Company name A–Z' },
  { value: 'symbol', label: 'Ticker A–Z' },
  { value: 'sector', label: 'Sector' },
  { value: 'change-desc', label: 'Biggest gainers today' },
  { value: 'change', label: 'Biggest losers today' },
]

/**
 * "Choose stocks": markets, indices and sectors on the left, a searchable list with prices on the right and the temporary
 * selection on top. Used by New portfolio and by the stock choice of Math and ML Optimization; `trayActions` replaces
 * the selection tray's default buttons (the optimization pages bring their own "use these stocks"). `priceCurrency` adds
 * a "Price (<currency>)" column converted with the server's latest reference rates (as in the trade proposal and the
 * holdings); `onPriceCurrencyChange` adds a "Prices in" picker (New portfolio: the same currency as the form above).
 * "Full screen" opens the same list, filters and pager over the window.
 */
export function StockChooser({ trayActions, priceCurrency, onPriceCurrencyChange }: {
  trayActions?: ReactNode; priceCurrency?: string; onPriceCurrencyChange?: (currency: string) => void
}) {
  const [scope, setScope] = useState<UniverseScope>({})
  const [searchText, setSearchText] = useState('')
  const search = useDeferredValue(searchText.trim())
  const [sort, setSort] = useState<StockSort>('name')
  const [withPricesOnly, setWithPricesOnly] = useState(false)
  const [selectedOnly, setSelectedOnly] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState<number>(50)
  const [detail, setDetail] = useState<string | null>(null)
  const [fullScreen, setFullScreen] = useState(false)
  const signal = useMapSignal()
  // Opening a stock makes its exchange city blink on the background map.
  useEffect(() => { if (detail) signal(countryOfSymbol(detail), 'blink') }, [detail, signal])
  const markets = useMarkets()
  const sectors = useSectors({ country: scope.country, index: scope.index })
  const selection = useSelection()
  // "Selected only" lists the temporary selection with the same filters, prices and paging.
  const reviewing = selectedOnly && selection.count > 0
  const stocks = useStocks({
    country: scope.country, index: scope.index, sector: scope.sector, search: search || undefined, sort,
    withPrices: withPricesOnly ? true : undefined, symbols: reviewing ? selection.symbols : undefined, page, pageSize,
  })
  const cycle = useQuoteRefreshCycle()
  const refreshQuotes = useRefreshQuotes()
  const qc = useQueryClient()

  const items = stocks.data?.items ?? []
  // Latest reference rates for the page's trading currencies against the chosen currency (one request per page).
  const fxInfo = useFxInfo()
  const foreign = [...new Set(items.map((s) => s.quote.currency || s.currency))].filter((c) => c && c !== priceCurrency).sort()
  const board = useFxBoard(priceCurrency, foreign)
  const boardRate = (c: string) => board.data?.rows.find((r) => r.quote === c)
  // One-month change and sparkline of the page's stocks, as on the watchlist (one batched request per page).
  const trends = useStockTrends(items.map((s) => s.symbol))
  // The longer ranges are full-screen columns: loaded only while full screen is open.
  const trends3m = useStockTrends(items.map((s) => s.symbol), '3M', fullScreen)
  const trends1y = useStockTrends(items.map((s) => s.symbol), '1Y', fullScreen)
  const pageSymbols = items.map((s) => s.symbol)
  const allOnPageSelected = pageSymbols.length > 0 && pageSymbols.every(selection.has)
  const cycleRunning = cycle.data?.status === 'Queued' || cycle.data?.status === 'Running'

  // When the sign-in refresh cycle finishes, reload the visible prices.
  const wasRunning = useRef(false)
  useEffect(() => {
    if (wasRunning.current && !cycleRunning) {
      qc.invalidateQueries({ queryKey: ['stocks'] })
      qc.invalidateQueries({ queryKey: ['markets'] })
    }
    wasRunning.current = cycleRunning
  }, [cycleRunning, qc])

  // Show cached prices immediately, then ask the backend once per page view for prices that are missing or older than
  // the cache window (the server batches provider requests and reuses fresh quotes).
  const requested = useRef(new Set<string>())
  const { mutate: requestQuotes } = refreshQuotes
  useEffect(() => {
    if (cycleRunning || !stocks.data || stocks.isPlaceholderData) return
    const symbols = symbolsNeedingRefresh(stocks.data.items.map((s) => s.quote)).filter((s) => !requested.current.has(s))
    if (symbols.length === 0) return
    symbols.forEach((s) => requested.current.add(s))
    requestQuotes({ symbols })
  }, [stocks.data, stocks.isPlaceholderData, cycleRunning, requestQuotes])

  const refreshPage = async () => {
    try {
      const result = await refreshQuotes.mutateAsync({ symbols: pageSymbols, force: true })
      const updated = result.items.filter((i) => i.outcome === 'Updated').length
      const failed = result.items.filter((i) => i.outcome === 'Failed' || i.outcome === 'Unsupported')
      if (failed.length === 0) toast.success(`Prices refreshed (${updated} updated, ${result.items.length - updated} already current)`)
      else toast.warning(`${updated} updated, ${failed.length} without a price`, { description: failed.slice(0, 3).map((f) => `${f.symbol}: ${f.message}`).join(' · ') })
    } catch (e) {
      toast.error('Could not refresh prices', { description: errorMessage(e) })
    }
  }

  const changeScope = (next: UniverseScope) => {
    setScope(next)
    setPage(1)
  }
  const clearAll = () => {
    setScope({}); setSearchText(''); setWithPricesOnly(false); setSelectedOnly(false); setPage(1)
  }

  // Every active narrowing as a removable chip, so a large catalogue never hides why a list is short.
  const chips: { key: string; label: string; remove: () => void }[] = []
  if (scope.index) chips.push({ key: 'index', label: indexLabel(markets.data, scope.index), remove: () => changeScope({ sector: scope.sector }) })
  else if (scope.region) chips.push({ key: 'region', label: scope.region, remove: () => changeScope({ sector: scope.sector }) })
  else if (scope.country) chips.push({ key: 'market', label: markets.data?.find((m) => m.country === scope.country)?.name ?? scope.country, remove: () => changeScope({ sector: scope.sector }) })
  if (scope.sector) chips.push({ key: 'sector', label: scope.sector, remove: () => changeScope({ ...scope, sector: undefined }) })
  if (search) chips.push({ key: 'search', label: `“${search}”`, remove: () => { setSearchText(''); setPage(1) } })
  if (withPricesOnly) chips.push({ key: 'prices', label: 'With price history', remove: () => { setWithPricesOnly(false); setPage(1) } })
  if (reviewing) chips.push({ key: 'selected', label: `Selected only (${selection.count})`, remove: () => { setSelectedOnly(false); setPage(1) } })

  const toSelected = (s: StockListItem) => ({ symbol: s.symbol, name: s.name, sector: s.sector, currency: s.currency })

  // Widths fit the list next to two sidebars: tick box, ticker and both prices always; "1 month" from md; Company from xl
  // (until then the name sits under the ticker); Sector and Index from 2xl (the industry also shows under the name).
  // Full screen has no sidebars: every column, the full company name and a wider sparkline use the room.
  const columnsFor = (full: boolean): Column<StockListItem>[] => [
    {
      key: 'select',
      header: <span className="sr-only">Select</span>,
      cell: (s) => (
        <Checkbox
          checked={selection.has(s.symbol)}
          onCheckedChange={() => selection.toggle(toSelected(s))}
          onClick={(e) => e.stopPropagation()}
          aria-label={`Select ${s.symbol}`}
        />
      ),
      className: 'w-10',
    },
    {
      key: 'symbol',
      header: 'Ticker',
      className: 'w-28',
      cell: (s) => (
        <div>
          <div className="font-semibold">{s.symbol}</div>
          <div className={cn('max-w-28 truncate text-[11px] text-foreground', full ? 'hidden' : 'xl:hidden')}>{s.name}</div>
          <div className="text-[11px] text-muted-foreground">{s.exchange ?? '—'}</div>
        </div>
      ),
    },
    {
      key: 'name',
      header: 'Company',
      cell: (s) => (
        <div className={full ? 'min-w-48' : 'max-w-56'}>
          <div className={full ? 'whitespace-normal' : 'truncate'}>{s.name ?? '—'}</div>
          <div className={cn('text-[11px] text-muted-foreground', full ? 'whitespace-normal' : 'truncate')}>{s.industry ?? s.sector ?? 'Unclassified'}</div>
        </div>
      ),
      className: full ? undefined : 'hidden xl:table-cell',
    },
    ...(priceCurrency ? [{
      key: 'converted', header: `Price (${priceCurrency})`, align: 'right' as const, className: 'w-32',
      cell: (s: StockListItem) => {
        const from = s.quote.currency || s.currency
        const row = boardRate(from)
        // Board rates are units of the stock's currency per one unit of the chosen currency; the cell wants the inverse.
        return <ConvertedPriceCell price={s.quote.price} from={from} to={priceCurrency} rate={row?.rate ? 1 / row.rate : null} rateDate={row?.observationDate ?? null} />
      },
    }] : []),
    { key: 'price', header: priceCurrency ? 'Original price' : 'Latest price', align: 'right', className: 'w-36', cell: (s) => <QuoteCell quote={s.quote} /> },
    {
      key: 'trend', header: '1 month', align: 'right', className: full ? 'w-32' : 'hidden w-28 md:table-cell',
      cell: (s) => trendCell(s.symbol, trends, 'one month', full),
    },
    // Full screen only: two longer ranges and the stored price history.
    ...(full ? [
      { key: 'trend3m', header: '3 months', align: 'right' as const, className: 'w-32', cell: (s: StockListItem) => trendCell(s.symbol, trends3m, 'three months', true) },
      { key: 'trend1y', header: '1 year', align: 'right' as const, className: 'w-32', cell: (s: StockListItem) => trendCell(s.symbol, trends1y, 'one year', true) },
      { key: 'history', header: 'Price history', className: 'w-36', cell: (s: StockListItem) => <HistoryCell stock={s} /> },
    ] : []),
    { key: 'sector', header: 'Sector', cell: (s) => <span className="text-xs whitespace-normal">{s.sector ?? 'Unclassified'}</span>, className: full ? 'w-32' : 'hidden w-28 2xl:table-cell' },
    {
      key: 'index',
      header: 'Index',
      cell: (s) => (
        <div className="flex flex-wrap gap-1">
          {s.indices.map((i) => <Badge key={i} variant="outline" className="h-5 px-1.5 text-[10px]">{indexLabel(markets.data, i)}</Badge>)}
        </div>
      ),
      className: full ? 'w-24' : 'hidden w-24 2xl:table-cell',
    },
  ]

  const scopeLabel = describeScope(markets.data, scope)

  // Selection and paging above and below the list (the same controls twice, so long pages need no scrolling back).
  const listControls = (where: 'top' | 'bottom') => (
    <div className="flex flex-wrap items-center justify-between gap-2" role="group" aria-label={`List controls (${where})`}>
      <div className="flex flex-wrap items-center gap-1.5">
        <Button variant="outline" size="sm" disabled={items.length === 0}
          onClick={() => allOnPageSelected ? pageSymbols.forEach(selection.remove) : selection.addMany(items.map(toSelected))}>
          {allOnPageSelected ? 'Deselect page' : 'Select page'}
        </Button>
        <Button variant={reviewing ? 'secondary' : 'outline'} size="sm" aria-pressed={reviewing} disabled={selection.count === 0}
          onClick={() => { setSelectedOnly(!reviewing); setPage(1) }}>
          Selected only{selection.count > 0 ? ` (${selection.count})` : ''}
        </Button>
      </div>
      <RangePager page={page} pageSize={pageSize} total={stocks.data?.totalCount ?? 0} totalPages={stocks.data?.totalPages ?? 1}
        onPage={setPage} onPageSize={(n) => { setPageSize(n); setPage(1) }} />
    </div>
  )

  // The list with its search, filters, chips and pager – inline, or over the whole window ("Full screen").
  const listBody = (inDialog: boolean) => (
    <>
      {/* Wraps instead of squeezing: the search keeps a usable width in the narrower portfolio layout too. */}
      <div className="flex flex-wrap items-center gap-3">
        <div className="relative min-w-64 flex-[1_1_18rem]">
          <Input
            value={searchText}
            onChange={(e) => { setSearchText(e.target.value); setPage(1) }}
            placeholder="Search company name or ticker (e.g. Siemens, 7203)"
            aria-label="Search company name or ticker"
          />
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Select value={sort} onValueChange={(v) => { setSort(v as StockSort); setPage(1) }} items={sortOptions}>
            <SelectTrigger className="w-48" aria-label="Sort stocks"><SelectValue /></SelectTrigger>
            <SelectContent>{sortOptions.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
          </Select>
          <div className="flex items-center gap-2">
            <Switch id={inDialog ? 'with-prices-full' : 'with-prices'} checked={withPricesOnly} onCheckedChange={(v) => { setWithPricesOnly(v); setPage(1) }} />
            <Label htmlFor={inDialog ? 'with-prices-full' : 'with-prices'} className="font-normal">With price history</Label>
            <InfoTip label="Price history">Stocks with stored daily prices can be used in analyses right away. Others need their history fetched first (selection tray → Fetch prices).</InfoTip>
          </div>
          {priceCurrency && onPriceCurrencyChange && (
            <div className="flex items-center gap-2">
              <Label htmlFor={inDialog ? 'prices-in-full' : 'prices-in'} className="font-normal">Prices in</Label>
              <CurrencySelect id={inDialog ? 'prices-in-full' : 'prices-in'} value={priceCurrency} onChange={onPriceCurrencyChange} supported={fxInfo.data?.currencies}
                label="Prices in" size="sm" className="w-28" />
            </div>
          )}
          <Button variant="outline" size="sm" onClick={refreshPage} disabled={items.length === 0 || refreshQuotes.isPending}>
            Refresh prices
          </Button>
          {!inDialog && <Button variant="outline" size="sm" onClick={() => setFullScreen(true)}>Full screen</Button>}
        </div>
      </div>
      <div className="flex flex-wrap items-center gap-1.5" aria-live="polite">
        <span className="text-xs text-muted-foreground tabular">
          {stocks.data ? `${num(stocks.data.totalCount, 0)} ${stocks.data.totalCount === 1 ? 'stock' : 'stocks'}` : ' '}
        </span>
        {chips.length === 0
          ? <span className="text-xs text-muted-foreground">· all markets</span>
          : chips.map((c) => (
            <Badge key={c.key} variant="secondary" className="h-6 gap-1 pr-1 pl-2 font-normal">
              {c.label}
              <button type="button" onClick={c.remove} aria-label={`Remove filter ${c.label}`} className="rounded-sm p-0.5 hover:bg-background/60">
                <X className="size-3" aria-hidden />
              </button>
            </Badge>
          ))}
        {chips.length > 1 && <Button variant="ghost" size="xs" onClick={clearAll}>Clear all</Button>}
        {stocks.isFetching && <MatrixLoader className="ml-1 inline size-3" aria-label="Loading" />}
      </div>
      {stocks.error ? <ErrorState error={stocks.error} onRetry={() => stocks.refetch()} /> : stocks.isLoading ? <LoadingBlock height="h-96" /> : (
        <>
          {listControls('top')}
          <DataTable
            rows={items}
            columns={columnsFor(inDialog)}
            rowKey={(s) => s.symbol}
            onRowClick={(s) => setDetail(s.symbol)}
            dense
            maxRows={inDialog ? undefined : 15}
            maxHeight={inDialog ? 'calc(100dvh - 19rem)' : undefined}
            caption={`Stocks – ${scopeLabel}`}
            empty={<EmptyState title="No stocks found" description="Try another market, sector or search term." />}
          />
          {listControls('bottom')}
        </>
      )}
    </>
  )

  return (
    <>
      <div className="grid gap-6 lg:grid-cols-[240px_minmax(0,1fr)]">
        {/* On a near-solid panel: the list sits next to the background map and must stay readable. */}
        <div className="map-surface hidden self-start rounded-xl p-2 lg:block">
          <UniverseNav markets={markets.data} sectors={sectors.data} scope={scope} onChange={changeScope} loading={markets.isLoading} />
        </div>
        {/* Small screens: the navigation is collapsed so the stock list stays visible. */}
        <details className="rounded-xl border bg-background px-3 py-2 lg:hidden">
          <summary className="cursor-pointer text-sm font-medium">Filter: {scopeLabel}</summary>
          <div className="mt-3"><UniverseNav markets={markets.data} sectors={sectors.data} scope={scope} onChange={changeScope} loading={markets.isLoading} /></div>
        </details>

        <div className="min-w-0 space-y-4">
          <SelectionTray actions={trayActions} />
          <PriceStatus run={cycle.data} pageRefreshing={refreshQuotes.isPending} />
          <Card className="py-4">
            <CardContent className="space-y-3 px-2 sm:px-4">
              {!fullScreen && listBody(false)}
            </CardContent>
          </Card>
          <p className="text-xs text-muted-foreground">
            Prices come from the server&apos;s data provider and are delayed quotes or last closes – never real-time. Each price shows when it
            applies and when it was retrieved; prices are converted to the trading currency (London quotes from pence to pounds).
            {priceCurrency && ` The “Price (${priceCurrency})” column converts them with the latest reference rate (its date on hover) – for comparing only, nothing is exchanged.`} Index
            memberships come from versioned Wikipedia constituent lists (CC BY-SA 4.0); the DAX 30 list is a historical composition. The index
            ETFs of the newer markets are a curated list, not index constituents.
          </p>
        </div>
      </div>

      <Dialog open={fullScreen} onOpenChange={setFullScreen}>
        <DialogContent className="top-2 left-2 flex h-[calc(100dvh-1rem)] w-[calc(100vw-1rem)] max-w-none translate-x-0 translate-y-0 flex-col gap-3 sm:max-w-none">
          <DialogHeader>
            <DialogTitle>Choose stocks – {scopeLabel}</DialogTitle>
            <DialogDescription>Select with the boxes; the selection is kept when you close. Esc closes.</DialogDescription>
          </DialogHeader>
          <div className="min-h-0 flex-1 space-y-3 overflow-y-auto">{fullScreen && listBody(true)}</div>
        </DialogContent>
      </Dialog>

      <StockDetailDialog symbol={detail} items={items} markets={markets.data} onNavigate={setDetail} onClose={() => setDetail(null)} />
    </>
  )
}

/** Status of the shared background price refresh (started at sign-in) and of on-page refreshes. */
function PriceStatus({ run, pageRefreshing }: { run: QuoteRefreshRun | undefined; pageRefreshing: boolean }) {
  if (!run && !pageRefreshing) return null
  const running = run?.status === 'Queued' || run?.status === 'Running'
  return (
    <div className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/30 px-3 py-2 text-xs" role="status" aria-live="polite">
      {running ? (
        <><MatrixLoader className="size-3.5" aria-hidden /> Updating prices for all markets in the background (started {age(run!.requestedAtUtc)})… Cached prices are shown meanwhile.</>
      ) : pageRefreshing ? (
        <><MatrixLoader className="size-3.5" aria-hidden /> Requesting current prices for this page…</>
      ) : run ? (
        <>
          <span>
            Prices last updated {age(run.completedAtUtc)}: {num(run.updated, 0)} refreshed, {num(run.unchanged, 0)} already current
            {run.failed > 0 && `, ${num(run.failed, 0)} unavailable`}.
          </span>
          {run.message && <InfoTip label="Refresh details">{run.message}</InfoTip>}
        </>
      ) : null}
    </div>
  )
}

function indexLabel(markets: Market[] | undefined, code: string) {
  return markets?.flatMap((m) => m.indices).find((i) => i.code === code)?.name ?? code
}

function describeScope(markets: Market[] | undefined, scope: UniverseScope) {
  const market = markets?.find((m) => m.country === scope.country)
  const parts = [scope.index ? indexLabel(markets, scope.index) : scope.region ?? market?.name ?? 'All markets']
  if (scope.sector) parts.push(scope.sector)
  return parts.join(' › ')
}

/** Change and sparkline of one stock over a range (as on the watchlist); "—" with the reason when history is missing. */
function trendCell(symbol: string, query: { data?: StockTrend[]; isLoading: boolean }, rangeLabel: string, full: boolean) {
  const t = query.data?.find((x) => x.symbol === symbol)?.period
  if (!t) return query.isLoading ? null : <span className="text-xs text-muted-foreground">—</span>
  if (t.change === null && t.sparkline.length < 2) return <span className="text-xs text-muted-foreground" title={t.limitation ?? 'Not enough stored history'}>—</span>
  return (
    <div className="flex flex-col items-end gap-0.5" title={t.change !== null ? `${t.basis}: ${t.baseDate} → ${t.endDate}` : t.limitation ?? undefined}>
      <ChangeText value={t.change} className="text-xs" />
      <Sparkline points={t.sparkline} label={`${symbol} ${t.basis.toLowerCase()} over ${rangeLabel}`} width={full ? 100 : 88} height={full ? 28 : 24} />
    </div>
  )
}

/** Stored daily prices of a stock and their span; fewer than 61 means analyses would exclude it until prices are fetched. */
function HistoryCell({ stock }: { stock: StockListItem }) {
  if (stock.observations === 0) return <span className="text-xs text-warn">None stored</span>
  const short = stock.observations < MIN_ANALYSIS_PRICES
  return (
    <div className="text-xs" title={short ? `Fewer than ${MIN_ANALYSIS_PRICES} stored prices: excluded from analyses until prices are fetched.` : undefined}>
      <div className={cn('tabular', short ? 'font-medium text-warn' : 'text-foreground')}>{num(stock.observations, 0)} days{short ? ' · too short' : ''}</div>
      <div className="text-[11px] whitespace-nowrap text-muted-foreground">{date(stock.firstDate)} – {date(stock.lastDate)}</div>
    </div>
  )
}

/** Same threshold as the selection tray: stocks with fewer stored prices are left out of analyses. */
const MIN_ANALYSIS_PRICES = 61
