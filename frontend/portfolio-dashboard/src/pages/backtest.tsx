import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { ChartCard, TimeSeriesChart, WeightsOverTimeChart, seriesColor, timeSeriesTable } from '@/components/charts'
import { DataTable } from '@/components/data-table'
import { Collapsible, DataBadge, EmptyState, ErrorState, InfoTip, LoadingBlock, MetricValue, PageHeader } from '@/components/common'
import { DataNotesList } from '@/components/strategy-insights'
import { errorMessage } from '@/lib/api'
import { portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { useBacktest, useBacktests, useCreateStrategyBacktest, usePortfolioStrategies } from '@/lib/queries'
import { NoStrategies, PortfolioPicker } from '@/components/strategy-picker'
import { dataNeeded, strategyKey, strategyRef } from '@/lib/strategies'
import { useMissingDataGuard } from '@/components/missing-data-dialog'
import type { Backtest, PortfolioStrategy } from '@/lib/types'
import { date, dateTime, money, num, objectiveLabels, pct, relativeTime } from '@/lib/format'
import { cn } from '@/lib/utils'
import { MatrixLoader, WorkingPanel } from '@/components/matrix-loader'

/** Backtests are hypothetical; shown in the page's tip and in every result's notes. */
const HYPOTHETICAL = 'Backtests use today’s list of symbols (survivorship bias) and historical estimates. They are hypothetical, not forecasts, and do not guarantee future returns.'

/** Where backtests open: inside a portfolio's workspace (`/portfolios/:id/backtest`) or on the page of all backtests. */
function backtestBase(portfolioId: string | undefined) {
  return portfolioId ? portfolioPath(portfolioId, 'backtest') : '/backtest'
}

/**
 * Backtests. Inside a portfolio (`/portfolios/:portfolioId/backtest`) only that portfolio's backtests are listed and new ones
 * belong to it; `/backtest` lists all of the user's backtests and lets the user choose the portfolio.
 */
export function BacktestPage() {
  const { backtestId, portfolioId: scoped } = useParams()
  const list = useBacktests(scoped)
  const activeId = backtestId ?? list.data?.items[0]?.id
  const bt = useBacktest(activeId)
  const base = backtestBase(scoped)

  return (
    <>
      {scoped
        ? <PageHeader level={2} titleHidden title="Backtesting" info={`Walk-forward historical simulation of one of this portfolio's Math or ML Optimizations, with periodic rebalancing, transaction costs and benchmark comparison. ${HYPOTHETICAL}`} />
        : <PageHeader title="Backtesting" info={`Walk-forward historical simulation of any Math or ML Optimization of your portfolios, with periodic rebalancing, transaction costs and benchmark comparison. ${HYPOTHETICAL}`} />}
      <div className="grid grid-cols-1 gap-6 xl:grid-cols-[360px_minmax(0,1fr)]">
        <div className="min-w-0 space-y-4">
          <BacktestAnOptimization fixedPortfolioId={scoped} />
          {list.data && list.data.items.length > 0 && (
            <Card>
              <CardHeader><CardTitle className="text-sm">Previous backtests</CardTitle></CardHeader>
              <CardContent className="max-h-72 space-y-1 overflow-y-auto">
                {list.data.items.map((b) => (
                  <Link key={b.id} to={`${base}/${b.id}`} className={`block rounded-md px-2.5 py-1.5 text-sm hover:bg-muted ${b.id === activeId ? 'bg-muted' : ''}`}>
                    <div className="flex items-center justify-between gap-2"><span className="truncate font-medium">{b.name}</span><StatusBadge status={b.status} /></div>
                    {b.strategyKind && <div className="text-[11px] text-muted-foreground">{b.strategyKind === 'MachineLearning' ? 'ML Optimization' : 'Math Optimization'}</div>}
                    <div className="text-xs text-muted-foreground">{dateTime(b.createdAtUtc)}</div>
                  </Link>
                ))}
              </CardContent>
            </Card>
          )}
        </div>
        <div className="min-w-0">
          {bt.isLoading || list.isLoading ? <LoadingBlock height="h-96" /> : bt.error ? <ErrorState error={bt.error} /> : bt.data ? <BacktestResults bt={bt.data} /> : (
            <EmptyState title="No backtests yet" description={scoped ? 'Choose one of this portfolio’s optimizations on the left and run its backtest.' : 'Choose a portfolio and one of its optimizations on the left.'} />
          )}
        </div>
      </div>
    </>
  )
}

const isoDate = (d: Date) => d.toISOString().slice(0, 10)

const kindOptions = [
  { value: 'all', label: 'All' },
  { value: 'Mathematical', label: 'Math' },
  { value: 'MachineLearning', label: 'ML' },
] as const

/**
 * Chooses one optimization – every Math and ML Optimization of the portfolio is listed and can be picked on its own – and
 * the simulation's period, capital, costs and rebalancing. `fixedPortfolioId`: inside a portfolio – its optimizations only,
 * no portfolio choice.
 */
function BacktestAnOptimization({ fixedPortfolioId }: { fixedPortfolioId?: string }) {
  const { portfolioId: defaultPortfolio, portfolios } = usePortfolioContext()
  const [portfolioId, setPortfolioId] = useState<string | undefined>(fixedPortfolioId ?? defaultPortfolio)
  const strategies = usePortfolioStrategies(portfolioId)
  const create = useCreateStrategyBacktest()
  const missingData = useMissingDataGuard()
  const navigate = useNavigate()
  const [kind, setKind] = useState<(typeof kindOptions)[number]['value']>('all')
  const [key, setKey] = useState<string | null>(null)
  const [end, setEnd] = useState(() => isoDate(new Date()))
  const [start, setStart] = useState(() => { const d = new Date(); d.setFullYear(d.getFullYear() - 3); return isoDate(d) })
  const [capital, setCapital] = useState(100_000)
  const [costs, setCosts] = useState(10)
  const [rebalance, setRebalance] = useState<'Monthly' | 'Quarterly'>('Monthly')
  const list = strategies.data ?? []
  const shown = list.filter((s) => kind === 'all' || s.kind === kind)
  const chosen = list.find((s) => strategyKey(s) === key)
  const portfolio = portfolios.find((p) => p.id === portfolioId)

  const submit = () => {
    const ref = strategyRef(key, list)
    if (!portfolioId || !ref || !chosen || !portfolio) return
    return missingData.guard(dataNeeded([chosen], start, end, portfolio.baseCurrency), async () => {
      try {
        const b = await create.mutateAsync({ portfolioId, strategy: ref, startDate: start, endDate: end, initialCapital: capital, transactionCostBps: costs, rebalanceFrequency: rebalance })
        toast.success('Backtest queued', { description: 'It runs in the background; results appear automatically.' })
        navigate(`${backtestBase(fixedPortfolioId)}/${b.id}`)
      } catch (e) {
        toast.error('Could not start backtest', { description: errorMessage(e) })
      }
    })
  }

  return (
    <Card role="region" aria-label="Backtest an optimization">
      <CardHeader>
        <CardTitle className="flex items-center gap-1.5 text-sm">
          <InfoTip of="Backtest an optimization">
            A Math Optimization is re-optimized with its stored settings at every rebalance; an ML Optimization is retrained
            walk-forward with its selected hyperparameters. Results before an ML Optimization&apos;s test period are marked as partly in-sample.
          </InfoTip>
        </CardTitle>
        <CardDescription className="text-xs">Pick one optimization, then the period to simulate.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        {!fixedPortfolioId && <PortfolioPicker value={portfolioId} onChange={(id) => { setPortfolioId(id); setKey(null) }} id="bt-portfolio" />}
        {portfolioId && (strategies.error ? <ErrorState error={strategies.error} /> : !strategies.data ? <LoadingBlock height="h-32" /> : list.length === 0
          ? <NoStrategies portfolioId={portfolioId} />
          : (
            <div className="space-y-2">
              <div className="flex items-center justify-between gap-2">
                <span className="text-xs font-medium">Optimization</span>
                <div className="flex gap-1" role="group" aria-label="Show optimizations">
                  {kindOptions.map((o) => {
                    const count = o.value === 'all' ? list.length : list.filter((s) => s.kind === o.value).length
                    return (
                      <Button key={o.value} type="button" size="xs" variant={kind === o.value ? 'secondary' : 'ghost'} aria-pressed={kind === o.value} onClick={() => setKind(o.value)}>
                        {o.label} <span className="text-muted-foreground tabular">{count}</span>
                      </Button>
                    )
                  })}
                </div>
              </div>
              <div role="radiogroup" aria-label="Optimization" className="max-h-80 divide-y overflow-y-auto rounded-lg border">
                {shown.length === 0 && <p className="px-3 py-4 text-center text-xs text-muted-foreground">None of this kind yet.</p>}
                {shown.map((s) => <OptimizationOption key={strategyKey(s)} strategy={s} checked={strategyKey(s) === key} onSelect={() => setKey(strategyKey(s))} />)}
              </div>
            </div>
          ))}
        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1.5"><Label htmlFor="bt-s-start" className="text-xs">Start</Label><Input id="bt-s-start" type="date" value={start} onChange={(e) => setStart(e.target.value)} /></div>
          <div className="space-y-1.5"><Label htmlFor="bt-s-end" className="text-xs">End</Label><Input id="bt-s-end" type="date" value={end} onChange={(e) => setEnd(e.target.value)} /></div>
          <div className="space-y-1.5"><Label htmlFor="bt-s-capital" className="text-xs">Initial capital</Label><Input id="bt-s-capital" type="number" value={capital} min={100} step={1000} onChange={(e) => setCapital(Number(e.target.value))} /></div>
          <div className="space-y-1.5"><Label htmlFor="bt-s-costs" className="text-xs">Transaction cost (bps)</Label><Input id="bt-s-costs" type="number" value={costs} min={0} max={500} onChange={(e) => setCosts(Number(e.target.value))} /></div>
          <div className="col-span-2 space-y-1.5">
            <Label className="text-xs">Rebalancing</Label>
            <Select value={rebalance} onValueChange={(v) => setRebalance(v as 'Monthly' | 'Quarterly')} items={[{ value: 'Monthly', label: 'Monthly' }, { value: 'Quarterly', label: 'Quarterly' }]}>
              <SelectTrigger className="w-full" aria-label="Rebalancing"><SelectValue /></SelectTrigger>
              <SelectContent><SelectItem value="Monthly">Monthly</SelectItem><SelectItem value="Quarterly">Quarterly</SelectItem></SelectContent>
            </Select>
          </div>
        </div>
        {end <= start && <p className="text-xs text-destructive">The end must be after the start.</p>}
        <Button className="w-full" onClick={submit} disabled={!chosen || end <= start || create.isPending || missingData.checking}>
          {(create.isPending || missingData.checking) && <MatrixLoader />} {create.isPending ? 'Rewinding…' : 'Rewind'}
        </Button>
        {missingData.dialog}
      </CardContent>
    </Card>
  )
}

/** One optimization in the list: kind, label, its settings in short, stock count and age. */
function OptimizationOption({ strategy: s, checked, onSelect }: { strategy: PortfolioStrategy; checked: boolean; onSelect: () => void }) {
  const ml = s.kind === 'MachineLearning'
  return (
    <button type="button" role="radio" aria-checked={checked} onClick={onSelect}
      className={cn('flex w-full items-start gap-2 border-l-2 border-transparent px-3 py-2 text-left text-sm hover:bg-muted', checked && 'border-primary bg-muted')}>
      <span className="min-w-0 flex-1">
        <span className="block truncate font-medium">{ml ? 'ML' : 'Math'} · {s.label}</span>
        <span className="block truncate text-xs text-muted-foreground">{s.detail} · {s.stocks} stocks · {relativeTime(s.createdAtUtc)}</span>
      </span>
      {s.usesSimulatedData && <DataBadge kind="simulated" />}
    </button>
  )
}

function StatusBadge({ status }: { status: string }) {
  return <Badge variant={status === 'Failed' ? 'destructive' : status === 'Succeeded' ? 'secondary' : 'outline'} className="text-[10px]">{status}</Badge>
}

function BacktestResults({ bt }: { bt: Backtest }) {
  const r = bt.result
  // Every amount is in the backtest's analysis currency (the portfolio's), stored with its configuration.
  const ccy = bt.config.currency ?? 'USD'
  const running = bt.status === 'Queued' || bt.status === 'Running'
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-lg font-semibold">{bt.name}</h2>
        <StatusBadge status={bt.status} />
        <DataBadge kind="backtest" />
        {bt.usesSimulatedData && <DataBadge kind="simulated" />}
      </div>

      {running && (
        <WorkingPanel title={bt.status === 'Queued' ? 'Waiting to start the backtest…' : 'Running the backtest…'} detail="Walk-forward simulation in the background; results appear automatically." />
      )}
      {bt.status === 'Failed' && <Alert variant="destructive"><AlertTitle>Backtest failed</AlertTitle><AlertDescription>{bt.error}</AlertDescription></Alert>}

      {r && (
        <>
          {/* The same translucent surface as the cards below. */}
          <div className="rounded-lg bg-card">
          <DataTable
            caption="Backtest summary"
            rows={r.metrics}
            rowKey={(m) => m.name}
            dense
            columns={[
              { key: 'n', header: 'Series', cell: (m) => <span className="flex items-center gap-2 font-medium"><span className="inline-block size-2.5 rounded-sm" style={{ background: seriesColor(r.metrics.indexOf(m)) }} aria-hidden />{m.name}</span> },
              { key: 't', header: 'Total return', align: 'right', cell: (m) => pct(m.totalReturn, 1, true) },
              { key: 'a', header: 'CAGR', align: 'right', cell: (m) => <MetricValue metric={m.annualizedReturn} format={(v) => pct(v)} /> },
              { key: 'v', header: 'Volatility', align: 'right', cell: (m) => <MetricValue metric={m.annualizedVolatility} format={(v) => pct(v)} /> },
              { key: 's', header: 'Sharpe', align: 'right', cell: (m) => <MetricValue metric={m.sharpeRatio} format={(v) => num(v)} /> },
              { key: 'so', header: 'Sortino', align: 'right', cell: (m) => <MetricValue metric={m.sortinoRatio} format={(v) => num(v)} /> },
              { key: 'd', header: 'Max DD', align: 'right', cell: (m) => <MetricValue metric={m.maxDrawdown} format={(v) => pct(v)} /> },
              { key: 'to', header: 'Turnover', align: 'right', cell: (m) => pct(m.totalTurnover, 0) },
              { key: 'c', header: 'Costs', align: 'right', cell: (m) => money(m.totalTransactionCosts, ccy, { decimals: 0 }) },
            ]}
          />
          </div>
          <div className="flex flex-wrap gap-4 text-xs text-muted-foreground">
            <span>Strategy beta: <MetricValue metric={r.beta} format={(v) => num(v)} /></span>
            <span>Tracking error: <MetricValue metric={r.trackingError} format={(v) => pct(v)} /></span>
            <span>Information ratio: <MetricValue metric={r.informationRatio} format={(v) => num(v)} /></span>
          </div>

          <ChartCard
            title="Equity curve"
            description={`Portfolio value (${ccy}), ${date(r.dates[0])} – ${date(r.dates[r.dates.length - 1])}`}
            legend={Object.keys(r.values).map((k, i) => ({ name: k, color: seriesColor(i), dashed: k.startsWith('Benchmark') }))}
            table={timeSeriesTable(r.dates, Object.entries(r.values).map(([name, values]) => ({ name, values })), (v) => money(v, ccy, { decimals: 0 }))}
          >
            <TimeSeriesChart dates={r.dates} series={Object.entries(r.values).map(([name, values]) => ({ name, values, dashed: name.startsWith('Benchmark') }))} format={(v) => money(v, ccy, { compact: true })} height={320} />
          </ChartCard>

          <ChartCard title="Drawdown" description="Decline from running peak, %"
            legend={Object.keys(r.drawdowns).map((k, i) => ({ name: k, color: seriesColor(i), dashed: k.startsWith('Benchmark') }))}
            table={timeSeriesTable(r.dates, Object.entries(r.drawdowns).map(([name, values]) => ({ name, values })), (v) => pct(v))}>
            <TimeSeriesChart dates={r.dates} series={Object.entries(r.drawdowns).map(([name, values]) => ({ name, values, dashed: name.startsWith('Benchmark') }))} format={(v) => pct(v, 0)} height={220} yDomain={['dataMin', 0]} />
          </ChartCard>

          {r.rebalances.length > 1 && (
            <Card>
              <CardHeader><CardTitle className="text-sm">Target weights at each rebalance</CardTitle><CardDescription className="text-xs">Set at the signal date’s close, executed at the next trading day’s close (up to five trading days later while an exchange is closed).</CardDescription></CardHeader>
              <CardContent><WeightsOverTimeChart points={r.rebalances.filter((e) => e.optimizationSucceeded).map((e) => ({ date: e.executionDate, weights: e.weights }))} /></CardContent>
            </Card>
          )}

          <MonthlyReturnsChart months={r.monthlyReturns} />

          <Collapsible title="Rebalance log" summary={`${r.rebalances.length} rebalances`}
            hint="Invested: sum of the target weights; the rest is cash (cash reserve, or a stock without a price on the execution day). “Held”: no new target – the optimization failed or no stock had a full lookback window, so the previous holdings were kept.">
            <DataTable
              caption="Rebalance log"
              rows={r.rebalances}
              rowKey={(e) => e.signalDate}
              dense
              columns={[
                { key: 's', header: 'Signal', cell: (e) => e.signalDate },
                { key: 'e', header: 'Executed', cell: (e) => e.executionDate },
                { key: 'n', header: 'Assets', align: 'right', cell: (e) => `${Object.keys(e.weights).length}/${e.eligibleSymbols.length}` },
                { key: 'i', header: 'Invested', align: 'right', cell: (e) => (!e.optimizationSucceeded && Object.keys(e.weights).length === 0 ? <span className="text-warn">held</span> : pct(Object.values(e.weights).reduce((a, w) => a + w, 0), 1)) },
                { key: 't', header: 'Turnover', align: 'right', cell: (e) => pct(e.turnover, 1) },
                { key: 'c', header: 'Costs', align: 'right', cell: (e) => money(e.transactionCosts, ccy) },
                { key: 'v', header: 'Value', align: 'right', cell: (e) => money(e.portfolioValue, ccy, { decimals: 0 }) },
                { key: 'm', header: 'Note', cell: (e) => <span className={e.optimizationSucceeded ? 'text-xs text-muted-foreground' : 'text-xs text-warn'}>{e.message === 'OK' ? '' : e.message}</span> },
              ]}
            />
          </Collapsible>
          <BacktestNotes bt={bt} warnings={r.warnings} methodology={r.methodology} />
        </>
      )}
    </div>
  )
}

/** The run's data notes, how it was simulated and why its results are hypothetical – one folded box at the end. */
function BacktestNotes({ bt, warnings, methodology }: { bt: Backtest; warnings: string[]; methodology: string }) {
  const c = bt.config
  const what = c.strategy ? `${c.strategy.kind === 'MachineLearning' ? 'ML' : 'Math'} Optimization “${c.strategy.label}”` : objectiveLabels[c.objective]
  const summary = [warnings.length > 0 && `${warnings.length} ${warnings.length === 1 ? 'note' : 'notes'}`, 'methodology'].filter(Boolean).join(' · ')
  return (
    <Collapsible title="Data notes & methodology" summary={summary}>
      <div className="space-y-3 leading-relaxed">
        <p>
          <span className="font-semibold">Simulated: </span>{what}, {date(c.startDate)} – {date(c.endDate)},
          {' '}{c.rebalanceFrequency === 'None' ? 'buy & hold' : `${c.rebalanceFrequency.toLowerCase()} rebalancing`}, {num(c.transactionCostBps, 0)} bps costs.
        </p>
        <p className="text-warn"><span className="font-semibold">Keep in mind. </span>{HYPOTHETICAL}</p>
        {warnings.length > 0 && (
          <section aria-label="Data notes" className="border-t pt-3">
            <h4 className="mb-1.5 font-semibold">Data notes</h4>
            <DataNotesList items={warnings} />
          </section>
        )}
        <section aria-label="Methodology" className="border-t pt-3">
          <h4 className="mb-1.5 font-semibold">Methodology</h4>
          <p className="text-muted-foreground">{methodology}</p>
        </section>
      </div>
    </Collapsible>
  )
}

/** Monthly returns of the strategy and its benchmark as lines (the table view lists every month). */
function MonthlyReturnsChart({ months }: { months: { year: number; month: number; portfolio: number | null; benchmark: number | null }[] }) {
  if (months.length === 0) return null
  const dates = months.map((m) => `${m.year}-${String(m.month).padStart(2, '0')}-01`)
  const series = [
    { name: 'Strategy', color: seriesColor(0), values: months.map((m) => m.portfolio) },
    ...(months.some((m) => m.benchmark !== null) ? [{ name: 'Benchmark', color: seriesColor(3), dashed: true, values: months.map((m) => m.benchmark) }] : []),
  ]
  return (
    <ChartCard title="Monthly returns" description="Return of each calendar month (the first and last month may be partial)"
      legend={series.map((x) => ({ name: x.name, color: x.color, dashed: x.dashed }))} table={timeSeriesTable(dates, series, (v) => pct(v, 1))}>
      <TimeSeriesChart dates={dates} series={series} format={(v) => pct(v, 0)} height={260} zeroLine />
    </ChartCard>
  )
}
