import { useId, useMemo, useState, type ReactNode } from 'react'
import { Badge } from '@/components/ui/badge'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Sheet, SheetContent, SheetDescription, SheetHeader, SheetTitle } from '@/components/ui/sheet'
import { Switch } from '@/components/ui/switch'
import { Label } from '@/components/ui/label'
import { SearchableTable, type Column } from '@/components/data-table'
import { TimeSeriesChart } from '@/components/charts'
import { DataBadge, LoadingBlock, Signed } from '@/components/common'
import { ConvertedPriceCell, FxNote, OriginalPriceCell } from '@/components/currency'
import { convertedPrice } from '@/lib/currency'
import { usePrices, useRisk, useTransactions } from '@/lib/queries'
import type { Holding, HoldingsResponse } from '@/lib/types'
import { date, dateTime, money, pct, qty } from '@/lib/format'

/**
 * The positions of one portfolio (Overview, since 2026-10-26 – the Holdings tab was merged into it), laid out like the
 * trade proposal's list: search, sector filter, closed and target-only positions, full screen, and a side panel per stock with its price, risk contribution and transactions.
 * Valued at the latest stored price in `currency`; prices stay in each stock's own currency.
 */
export function HoldingsTable({ data, loading, currency: ccy, account, portfolioId }: {
  data: HoldingsResponse | undefined; loading: boolean; currency: string; account: string; portfolioId: string
}) {
  const [sector, setSector] = useState<string>('all')
  const closedId = useId()
  const [showClosed, setShowClosed] = useState(false)
  const [selected, setSelected] = useState<Holding | null>(null)

  const sectors = useMemo(() => [...new Set((data?.holdings ?? []).map((h) => h.sector ?? 'Unclassified'))].sort(), [data])
  const rows = useMemo(() => (data?.holdings ?? []).filter((h) =>
    (showClosed || h.quantity > 0) && (sector === 'all' || (h.sector ?? 'Unclassified') === sector),
  ), [data, sector, showClosed])
  const open = rows.filter((h) => h.quantity > 0).length

  // Same layout as the trade proposal: price in the valuation currency with the rate, then the original price with its date.
  const columns: Column<Holding>[] = [
    {
      key: 'symbol', header: 'Symbol', sortValue: (h) => h.symbol,
      cell: (h) => (
        <div>
          <div className="font-medium">{h.symbol}</div>
          <div className="max-w-48 truncate text-xs text-muted-foreground">{h.name}</div>
        </div>
      ),
    },
    {
      key: 'price', header: `Price (${ccy})`, align: 'right', sortValue: (h) => convertedPrice(h.latestPrice, h.currency, ccy, h.fxRate),
      cell: (h) => <ConvertedPriceCell price={h.latestPrice} from={h.currency} to={ccy} rate={h.fxRate} rateDate={h.fxRateDate} />,
    },
    {
      key: 'orig', header: 'Original price', align: 'right', sortValue: (h) => h.latestPrice,
      cell: (h) => <OriginalPriceCell price={h.latestPrice} currency={h.currency} priceDate={h.priceDate} stale={h.priceIsStale} />,
    },
    { key: 'qty', header: 'Quantity', align: 'right', sortValue: (h) => h.quantity, cell: (h) => qty(h.quantity) },
    {
      key: 'mv', header: `Market value (${ccy})`, align: 'right', sortValue: (h) => h.marketValue,
      cell: (h) => h.valuationProblem && h.quantity > 0
        ? <span className="text-xs text-warn" title={h.valuationProblem}>Cannot be valued</span>
        : money(h.marketValue, ccy),
    },
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

  const toolbar = (
    <>
      <Select value={sector} onValueChange={(v) => setSector(String(v))} items={[{ value: 'all', label: 'All sectors' }, ...sectors.map((x) => ({ value: x, label: x }))]}>
        <SelectTrigger className="bg-card sm:w-56" aria-label="Filter by sector"><SelectValue /></SelectTrigger>
        <SelectContent>
          <SelectItem value="all">All sectors</SelectItem>
          {sectors.map((x) => <SelectItem key={x} value={x}>{x}</SelectItem>)}
        </SelectContent>
      </Select>
      <div className="flex items-center gap-2">
        <Switch id={closedId} checked={showClosed} onCheckedChange={setShowClosed} />
        <Label htmlFor={closedId} className="text-sm font-normal">Show closed & target-only</Label>
      </div>
    </>
  )

  return (
    <>
      {/* The app's standard long list (as Proposed trades): search, about ten rows under a fixed header, full screen. */}
      <div className="map-surface rounded-xl p-3">
        {loading ? <LoadingBlock /> : (
          <SearchableTable
            label="holdings"
            caption="Holdings"
            rows={rows}
            columns={columns}
            rowKey={(h) => String(h.assetId)}
            initialSort={{ key: 'mv', dir: 'desc' }}
            onRowClick={setSelected}
            dense maxRows={10} pageSize={50} fullScreen="Holdings"
            toolbar={toolbar}
            placeholder="Search symbol or name"
            matches={(h, q) => h.symbol.toLowerCase().includes(q) || (h.name ?? '').toLowerCase().includes(q)}
            breakdown={`${open.toLocaleString()} open position${open === 1 ? '' : 's'}${rows.length > open ? ` · ${(rows.length - open).toLocaleString()} closed or target-only` : ''}`}
            empty={sector !== 'all' ? 'No holdings in this sector.' : 'No holdings yet. Positions appear here after you confirm a simulated trade proposal.'}
          />
        )}
      </div>

      <HoldingSheet holding={selected} onClose={() => setSelected(null)} currency={ccy} account={account} portfolioId={portfolioId} />
    </>
  )
}

function HoldingSheet({ holding, onClose, currency, account, portfolioId }: { holding: Holding | null; onClose: () => void; currency: string; account: string; portfolioId: string }) {
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
                <Stat label="Latest price" value={<>{money(holding.latestPrice, holding.currency)} ({date(holding.priceDate)}){holding.fxRate !== null && <div><FxNote rate={holding.fxRate} from={holding.currency} to={currency} on={holding.fxRateDate} /></div>}</>} />
                <Stat label="Weight / target" value={`${pct(holding.weight)} / ${pct(holding.targetWeight)}`} />
                <Stat label="Unrealized P&L" value={<Signed value={holding.unrealizedPnl}>{money(holding.unrealizedPnl, currency, { signed: true })} ({pct(holding.unrealizedReturn, 2, true)})</Signed>} />
                <Stat label="Contribution to risk" value={contribution?.riskContribution !== undefined && contribution?.riskContribution !== null ? pct(contribution.riskContribution) : '—'} />
                <Stat label="Asset volatility (1y)" value={contribution ? pct(contribution.volatility) : '—'} />
              </div>

              <div>
                <h3 className="mb-2 text-sm font-semibold">Price, last 12 months <span className="font-normal text-muted-foreground">(raw close, {holding.currency})</span></h3>
                {prices.isLoading ? <LoadingBlock height="h-48" /> : prices.data && prices.data.items.length > 1 ? (
                  <TimeSeriesChart
                    dates={prices.data.items.map((p) => p.date)}
                    series={[{ name: holding.symbol, values: prices.data.items.map((p) => p.close) }]}
                    format={(v) => money(v, holding.currency, { decimals: 0 })}
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
                        {qty(t.quantity)} @ {money(t.price, t.currency)}{t.fxRate !== 1 && <FxNote rate={t.fxRate} from={t.currency} to={account} on={t.fxRateDate} />}
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
