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
import { DataTable, RangePager, type Column } from '@/components/data-table'
import { EmptyState, ErrorState, InfoTip, LoadingBlock } from '@/components/common'
import { QuoteCell, SelectionTray } from '@/components/stock-selection'
import { UniverseNav, type UniverseScope } from '@/components/universe-nav'
import { errorMessage } from '@/lib/api'
import {
  useMarkets, useQuoteRefreshCycle, useRefreshQuotes, useSectors, useStocks, type StockSort,
} from '@/lib/queries'
import { age, symbolsNeedingRefresh } from '@/lib/quote-status'
import { useSelection } from '@/lib/selection'
import type { Market, QuoteRefreshRun, StockListItem } from '@/lib/types'
import { num } from '@/lib/format'
import { countryOfSymbol } from '@/lib/markets'
import { useMapSignal } from '@/lib/world-map'
import { MatrixLoader } from '@/components/matrix-loader'


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
 * the selection tray's default buttons (the optimization pages bring their own "use these stocks").
 */
export function StockChooser({ trayActions }: { trayActions?: ReactNode }) {
  const [scope, setScope] = useState<UniverseScope>({})
  const [searchText, setSearchText] = useState('')
  const search = useDeferredValue(searchText.trim())
  const [sort, setSort] = useState<StockSort>('name')
  const [withPricesOnly, setWithPricesOnly] = useState(false)
  const [selectedOnly, setSelectedOnly] = useState(false)
  const [page, setPage] = useState(1)
  const [pageSize, setPageSize] = useState<number>(50)
  const [detail, setDetail] = useState<string | null>(null)
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

  const columns: Column<StockListItem>[] = [
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
      cell: (s) => (
        <div>
          <div className="font-semibold">{s.symbol}</div>
          <div className="max-w-24 truncate text-[11px] text-foreground sm:hidden">{s.name}</div>
          <div className="text-[11px] text-muted-foreground">{s.exchange ?? '—'}</div>
        </div>
      ),
    },
    {
      key: 'name',
      header: 'Company',
      cell: (s) => (
        <div className="max-w-64">
          <div className="truncate">{s.name ?? '—'}</div>
          <div className="truncate text-[11px] text-muted-foreground">{s.industry ?? s.sector ?? 'Unclassified'}</div>
        </div>
      ),
      className: 'hidden sm:table-cell',
    },
    { key: 'price', header: 'Latest price', align: 'right', cell: (s) => <QuoteCell quote={s.quote} /> },
    { key: 'sector', header: 'Sector', cell: (s) => <span className="text-xs">{s.sector ?? 'Unclassified'}</span>, className: 'hidden xl:table-cell' },
    {
      key: 'index',
      header: 'Index',
      cell: (s) => (
        <div className="flex flex-wrap gap-1">
          {s.indices.map((i) => <Badge key={i} variant="outline" className="h-5 px-1.5 text-[10px]">{indexLabel(markets.data, i)}</Badge>)}
        </div>
      ),
      className: 'hidden lg:table-cell',
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
                    <Switch id="with-prices" checked={withPricesOnly} onCheckedChange={(v) => { setWithPricesOnly(v); setPage(1) }} />
                    <Label htmlFor="with-prices" className="font-normal">With price history</Label>
                    <InfoTip label="Price history">Stocks with stored daily prices can be used in analyses right away. Others need their history fetched first (selection tray → Fetch prices).</InfoTip>
                  </div>
                  <Button variant="outline" size="sm" onClick={refreshPage} disabled={items.length === 0 || refreshQuotes.isPending}>
                    Refresh prices
                  </Button>
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
                    columns={columns}
                    rowKey={(s) => s.symbol}
                    onRowClick={(s) => setDetail(s.symbol)}
                    dense
                    caption={`Stocks – ${scopeLabel}`}
                    empty={<EmptyState title="No stocks found" description="Try another market, sector or search term." />}
                  />
                  {listControls('bottom')}
                </>
              )}
            </CardContent>
          </Card>
          <p className="text-xs text-muted-foreground">
            Prices come from the server&apos;s data provider and are delayed quotes or last closes – never real-time. Each price shows when it
            applies and when it was retrieved; prices are converted to the trading currency (London quotes from pence to pounds). Index
            memberships come from versioned Wikipedia constituent lists (CC BY-SA 4.0); the DAX 30 list is a historical composition. The index
            ETFs of the newer markets are a curated list, not index constituents.
          </p>
        </div>
      </div>

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
