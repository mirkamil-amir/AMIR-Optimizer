import { useMemo, useState, type ReactNode } from 'react'
import { Search } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Switch } from '@/components/ui/switch'
import { Label } from '@/components/ui/label'
import { DataTable, type Column } from '@/components/data-table'
import { TimeSeriesChart } from '@/components/charts'
import { DataBadge, EmptyState, ErrorState, LoadingBlock, MetricCard, PageHeader, Signed, Warnings } from '@/components/common'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useHoldings, usePrices, useRisk, useTransactions } from '@/lib/queries'
import type { Holding } from '@/lib/types'
import { date, dateTime, money, pct, qty } from '@/lib/format'
import { NoPortfolio } from './shared'

export function HoldingsPage() {
  const { portfolioId, portfolio, isLoading } = usePortfolioContext()
  const { data, error, refetch, isLoading: loading } = useHoldings(portfolioId)
  const [search, setSearch] = useState('')
  const [sector, setSector] = useState<string>('all')
  const [showClosed, setShowClosed] = useState(false)
  const [selected, setSelected] = useState<Holding | null>(null)
  const ccy = portfolio?.baseCurrency ?? 'USD'

  const sectors = useMemo(() => [...new Set((data?.holdings ?? []).map((h) => h.sector ?? 'Unclassified'))].sort(), [data])
  const rows = useMemo(() => (data?.holdings ?? []).filter((h) =>
    (showClosed || h.quantity > 0) &&
    (sector === 'all' || (h.sector ?? 'Unclassified') === sector) &&
    (search === '' || h.symbol.toLowerCase().includes(search.toLowerCase()) || (h.name ?? '').toLowerCase().includes(search.toLowerCase())),
  ), [data, search, sector, showClosed])

  if (!isLoading && !portfolioId) return <NoPortfolio />
  const s = data?.summary

  const columns: Column<Holding>[] = [
    {
      key: 'symbol', header: 'Symbol', sortValue: (h) => h.symbol,
      cell: (h) => (
        <div>
          <div className="flex items-center gap-1.5 font-medium">{h.symbol}{h.priceIsStale && <DataBadge kind="stale" />}</div>
          <div className="max-w-48 truncate text-xs text-muted-foreground">{h.name}</div>
        </div>
      ),
    },
    { key: 'qty', header: 'Quantity', align: 'right', sortValue: (h) => h.quantity, cell: (h) => qty(h.quantity) },
    { key: 'price', header: 'Price', align: 'right', sortValue: (h) => h.latestPrice, cell: (h) => <span title={h.priceDate ? `Close of ${date(h.priceDate)}` : undefined}>{money(h.latestPrice, ccy)}</span> },
    { key: 'mv', header: 'Market value', align: 'right', sortValue: (h) => h.marketValue, cell: (h) => money(h.marketValue, ccy) },
    { key: 'w', header: 'Weight', align: 'right', sortValue: (h) => h.weight, cell: (h) => pct(h.weight) },
    { key: 'cost', header: 'Avg cost', align: 'right', sortValue: (h) => h.averageCost, cell: (h) => <span title={`Cost basis ${money(h.costBasis, ccy)}`}>{h.quantity > 0 ? money(h.averageCost, ccy) : '—'}</span> },
    {
      key: 'pnl', header: 'Unrealized P&L', align: 'right', sortValue: (h) => h.unrealizedPnl,
      cell: (h) => (
        <div>
          <Signed value={h.unrealizedPnl}>{money(h.unrealizedPnl, ccy, { signed: true })}</Signed>
          <div className="text-xs"><Signed value={h.unrealizedReturn}>{pct(h.unrealizedReturn, 2, true)}</Signed></div>
        </div>
      ),
    },
    { key: 'real', header: 'Realized', align: 'right', sortValue: (h) => h.realizedPnl, cell: (h) => <Signed value={h.realizedPnl}>{money(h.realizedPnl, ccy, { signed: true })}</Signed> },
    { key: 'tw', header: 'Target', align: 'right', sortValue: (h) => h.targetWeight, cell: (h) => pct(h.targetWeight) },
    {
      key: 'drift', header: 'Drift', align: 'right', sortValue: (h) => h.weightDeviation,
      cell: (h) => h.weightDeviation === null ? '—' : (
        <span className={Math.abs(h.weightDeviation) >= 0.02 ? 'font-medium text-warn' : 'text-muted-foreground'}>{pct(h.weightDeviation, 2, true)}</span>
      ),
    },
  ]

  return (
    <>
      <PageHeader
        title="Holdings"
        description="Positions in the simulated portfolio, valued at the latest stored price."
        actions={s && (s.usesSimulatedData ? <DataBadge kind="simulated" /> : <DataBadge kind="cached" label={`${s.priceSource} · ${date(s.valuationDate)}`} />)}
      />
      {error && <ErrorState error={error} onRetry={() => refetch()} />}

      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        <MetricCard label="Market value" loading={!s} value={money(s?.marketValue, ccy)} />
        <MetricCard label="Cash" loading={!s} value={money(s?.cashBalance, ccy)} sub={`${pct(1 - (s?.investedFraction ?? 0), 1)} of total`} />
        <MetricCard label="Total value" loading={!s} value={money(s?.totalValue, ccy)} />
        <MetricCard label="Cost basis" loading={!s} value={money(s?.totalCostBasis, ccy)} hint="Weighted-average cost of open positions including buy commissions." />
        <MetricCard label="Unrealized P&L" loading={!s} tone="signed" numeric={s?.unrealizedPnl} value={money(s?.unrealizedPnl, ccy, { signed: true })} />
      </div>
      <div className="mt-4"><Warnings items={s?.warnings} /></div>

      <div className="mt-6 flex flex-col gap-3 sm:flex-row sm:items-center">
        <div className="relative sm:w-72">
          <Search className="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-muted-foreground" aria-hidden />
          <Input placeholder="Search symbol or name" aria-label="Search holdings" className="pl-8" value={search} onChange={(e) => setSearch(e.target.value)} />
        </div>
        <Select value={sector} onValueChange={(v) => setSector(String(v))} items={[{ value: 'all', label: 'All sectors' }, ...sectors.map((x) => ({ value: x, label: x }))]}>
          <SelectTrigger className="sm:w-56" aria-label="Filter by sector"><SelectValue /></SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All sectors</SelectItem>
            {sectors.map((x) => <SelectItem key={x} value={x}>{x}</SelectItem>)}
          </SelectContent>
        </Select>
        <div className="flex items-center gap-2">
          <Switch id="closed" checked={showClosed} onCheckedChange={setShowClosed} />
          <Label htmlFor="closed" className="text-sm font-normal">Show closed & target-only</Label>
        </div>
      </div>

      <div className="mt-4">
        {loading ? <LoadingBlock /> : (
          <DataTable
            caption="Holdings"
            rows={rows}
            columns={columns}
            rowKey={(h) => String(h.assetId)}
            initialSort={{ key: 'mv', dir: 'desc' }}
            onRowClick={setSelected}
            empty={<EmptyState title={search || sector !== 'all' ? 'No matching holdings' : 'No holdings yet'} description="Positions appear here after you confirm a simulated trade proposal." />}
          />
        )}
      </div>

      <HoldingSheet holding={selected} onClose={() => setSelected(null)} currency={ccy} portfolioId={portfolioId} />
    </>
  )
}

function HoldingSheet({ holding, onClose, currency, portfolioId }: { holding: Holding | null; onClose: () => void; currency: string; portfolioId?: string }) {
  const oneYearAgo = useMemo(() => new Date(Date.now() - 365 * 864e5).toISOString().slice(0, 10), [])
  const prices = usePrices(holding?.symbol, oneYearAgo)
  const tx = useTransactions(holding ? portfolioId : undefined, 1, holding?.symbol, 20)
  const risk = useRisk(portfolioId)
  const contribution = risk.data?.contributions.find((c) => c.symbol === holding?.symbol)

  return (
    <Sheet open={!!holding} onOpenChange={(o) => !o && onClose()}>
      <SheetContent className="w-full overflow-y-auto sm:max-w-xl">
        {holding && (
          <>
            <SheetHeader>
              <SheetTitle>{holding.symbol}</SheetTitle>
              <SheetDescription>{holding.name}{holding.sector ? ` · ${holding.sector}` : ''}</SheetDescription>
            </SheetHeader>
            <div className="space-y-5 px-4 pb-6">
              <div className="grid grid-cols-2 gap-3 text-sm">
                <Stat label="Quantity" value={qty(holding.quantity)} />
                <Stat label="Market value" value={money(holding.marketValue, currency)} />
                <Stat label="Average cost" value={money(holding.averageCost, currency)} />
                <Stat label="Latest price" value={`${money(holding.latestPrice, currency)} (${date(holding.priceDate)})`} />
                <Stat label="Weight / target" value={`${pct(holding.weight)} / ${pct(holding.targetWeight)}`} />
                <Stat label="Unrealized P&L" value={<Signed value={holding.unrealizedPnl}>{money(holding.unrealizedPnl, currency, { signed: true })} ({pct(holding.unrealizedReturn, 2, true)})</Signed>} />
                <Stat label="Contribution to risk" value={contribution?.riskContribution !== undefined && contribution?.riskContribution !== null ? pct(contribution.riskContribution) : '—'} />
                <Stat label="Asset volatility (1y)" value={contribution ? pct(contribution.volatility) : '—'} />
              </div>

              <div>
                <h3 className="mb-2 text-sm font-semibold">Price, last 12 months <span className="font-normal text-muted-foreground">(raw close, {currency})</span></h3>
                {prices.isLoading ? <LoadingBlock height="h-48" /> : prices.data && prices.data.items.length > 1 ? (
                  <TimeSeriesChart
                    dates={prices.data.items.map((p) => p.date)}
                    series={[{ name: holding.symbol, values: prices.data.items.map((p) => p.close) }]}
                    format={(v) => money(v, currency, { decimals: 0 })}
                    height={200}
                  />
                ) : <p className="text-sm text-muted-foreground">No price history.</p>}
                {prices.data?.items[0]?.isSimulated && <div className="mt-2"><DataBadge kind="simulated" /></div>}
              </div>

              <div>
                <h3 className="mb-2 text-sm font-semibold">Transactions</h3>
                {tx.data?.items.length === 0 && <p className="text-sm text-muted-foreground">None.</p>}
                <ul className="divide-y text-sm">
                  {tx.data?.items.map((t) => (
                    <li key={t.id} className="flex items-center justify-between py-1.5 tabular">
                      <span className="flex items-center gap-2">
                        <Badge variant="outline" className={t.side === 'Buy' ? 'text-gain' : 'text-loss'}>{t.side}</Badge>
                        {qty(t.quantity)} @ {money(t.price, currency)}
                      </span>
                      <span className="text-xs text-muted-foreground">{dateTime(t.executedAtUtc)}</span>
                    </li>
                  ))}
                </ul>
              </div>
            </div>
          </>
        )}
      </SheetContent>
    </Sheet>
  )
}

function Stat({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="rounded-md border px-3 py-2">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="mt-0.5 font-medium tabular">{value}</div>
    </div>
  )
}
