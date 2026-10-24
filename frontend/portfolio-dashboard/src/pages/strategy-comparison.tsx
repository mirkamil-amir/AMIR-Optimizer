import { useState } from 'react'
import { useNavigate, useParams } from 'react-router'
import { toast } from 'sonner'
import { AlertTriangle, CheckCircle2, GitCompareArrows, Loader2, Play, Trash2 } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Progress } from '@/components/ui/progress'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { ChartCard, TimeSeriesChart, seriesColor, timeSeriesTable } from '@/components/charts'
import { GroupedBarChart, RiskReturnScatter, SignedBarChart, groupedBarTable } from '@/components/analysis-charts'
import { Collapsible, DataBadge, EmptyState, ErrorState, InfoTip, LoadingBlock, PageHeader, SectionTitle, Warnings } from '@/components/common'
import { PerformanceTable } from '@/components/performance-table'
import { Pager } from '@/components/data-table'
import { StrategyInsights } from '@/components/strategy-insights'
import { interpretComparison } from '@/lib/interpretation'
import { errorMessage } from '@/lib/api'
import { useComparison, useComparisons, useCreateComparison, useDeleteComparison, usePortfolioStrategies } from '@/lib/queries'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { dataNeeded, defaultStart, hybridPossible, strategyKey, strategyRef } from '@/lib/strategies'
import { NoStrategies, PortfolioPicker, StrategySelect } from '@/components/strategy-picker'
import { useMissingDataGuard } from '@/components/missing-data-dialog'
import type { ComparisonStrategy, StrategyComparison, StrategyComparisonRequest } from '@/lib/types'
import { date, money, pct, relativeTime } from '@/lib/format'
import { cn } from '@/lib/utils'

/** Fixed colour per strategy kind (follows the entity, never its rank). */
const strategyColor: Record<string, string> = {
  a: seriesColor(0),
  b: seriesColor(1),
  // Comparisons stored before 2026-10-24 keyed their two strategies by kind.
  math: seriesColor(0),
  ml: seriesColor(1),
  hybrid: seriesColor(2),
  equal: seriesColor(3),
  benchmark: seriesColor(4),
}

export function StrategyComparisonPage() {
  const { comparisonId } = useParams()
  const list = useComparisons()
  const activeId = comparisonId ?? list.data?.items[0]?.id
  return (
    <>
      <PageHeader
        title="Strategy comparison"
        description="Any two optimizations of a portfolio – Math vs Math, ML vs ML or Math vs ML – next to equal weight and the benchmark – re-evaluated walk-forward on one shared simulator with identical dates, costs, capital and rebalancing."
        actions={<Badge variant="outline" className="gap-1"><GitCompareArrows className="size-3" /> Shared evaluation engine</Badge>}
      />
      <div className="grid gap-6 xl:grid-cols-[380px_minmax(0,1fr)]">
        <div className="space-y-4">
          <NewComparisonCard />
          <ComparisonHistory activeId={activeId} />
        </div>
        <div className="min-w-0">
          {activeId ? <ComparisonDetail id={activeId} /> : list.isLoading ? <LoadingBlock height="h-96" /> : (
            <EmptyState icon={<GitCompareArrows className="size-8" />} title="No comparisons yet"
              description="You need one successful optimization run and one completed ML experiment. Choose both on the left." />
          )}
        </div>
      </div>
    </>
  )
}

function NewComparisonCard() {
  const { portfolioId: defaultPortfolio, portfolios } = usePortfolioContext()
  const [portfolioId, setPortfolioId] = useState<string | undefined>(defaultPortfolio)
  const strategies = usePortfolioStrategies(portfolioId)
  const create = useCreateComparison()
  const missingData = useMissingDataGuard()
  const navigate = useNavigate()
  const [a, setA] = useState<string | null>(null)
  const [b, setB] = useState<string | null>(null)
  const [start, setStart] = useState('')
  const [end, setEnd] = useState('')
  const [costs, setCosts] = useState(10)
  const [capital, setCapital] = useState(100_000)
  const [rebalance, setRebalance] = useState<'Monthly' | 'Quarterly'>('Monthly')
  const [universe, setUniverse] = useState<'Intersection' | 'Union'>('Intersection')
  const [hybrid, setHybrid] = useState(false)

  const list = strategies.data ?? []
  const strategyA = list.find((s) => strategyKey(s) === a)
  const strategyB = list.find((s) => strategyKey(s) === b)
  const canHybrid = hybridPossible(strategyA, strategyB)
  const portfolio = portfolios.find((p) => p.id === portfolioId)
  const choosePortfolio = (id: string) => {
    setPortfolioId(id)
    setA(null)
    setB(null)
  }

  const submit = () => {
    const refA = strategyRef(a, list)
    const refB = strategyRef(b, list)
    if (!portfolioId || !refA || !refB || !strategyA || !strategyB || !portfolio) return
    const body: StrategyComparisonRequest = {
      portfolioId, strategyA: refA, strategyB: refB, startDate: start || null, endDate: end || null, initialCapital: capital,
      benchmarkSymbol: strategyA.benchmarkSymbol, transactionCostBps: costs, rebalance, universe, includeHybrid: canHybrid && hybrid,
    }
    // Missing prices or rates for both strategies (incl. their history before the start) are offered for download first.
    const from = start || defaultStart()
    return missingData.guard(dataNeeded([strategyA, strategyB], from, end || null, portfolio.baseCurrency), async () => {
      try {
        const c = await create.mutateAsync(body)
        toast.success('Comparison queued')
        navigate(`/strategy-comparison/${c.summary.id}`)
      } catch (e) {
        toast.error('Could not start the comparison', { description: errorMessage(e) })
      }
    })
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">New comparison</CardTitle>
        <CardDescription className="text-xs">
          Any two optimizations of one portfolio – Math vs Math, ML vs ML or Math vs ML. Both are re-run walk-forward with their
          stored settings; nothing is compared from stored results.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-4">
        <PortfolioPicker value={portfolioId} onChange={choosePortfolio} id="cmp-portfolio" />
        {portfolioId && (strategies.error ? <ErrorState error={strategies.error} /> : !strategies.data ? <LoadingBlock height="h-16" /> : list.length === 0 ? (
          <NoStrategies portfolioId={portfolioId} />
        ) : (
          <>
            <StrategySelect strategies={list} value={a} onChange={setA} label="Strategy A" disabledKey={b} />
            <StrategySelect strategies={list} value={b} onChange={setB} label="Strategy B" disabledKey={a} />
            {list.length < 2 && <p className="text-xs text-muted-foreground">A comparison needs two optimizations of this portfolio.</p>}
          </>
        ))}
        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1.5">
            <Label htmlFor="cmp-start" className="flex items-center gap-1 text-xs">Start<InfoTip>Default: the latest start of an ML strategy's test period, so no strategy's choices used the evaluation data (without ML: three years ago). Earlier starts are flagged as partly in-sample.</InfoTip></Label>
            <Input id="cmp-start" type="date" value={start} onChange={(e) => setStart(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="cmp-end" className="text-xs">End (default: today)</Label>
            <Input id="cmp-end" type="date" value={end} onChange={(e) => setEnd(e.target.value)} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="cmp-capital" className="text-xs">Initial capital</Label>
            <Input id="cmp-capital" type="number" value={capital} min={1000} step={1000} onChange={(e) => setCapital(Number(e.target.value))} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="cmp-costs" className="text-xs">Transaction cost (bps)</Label>
            <Input id="cmp-costs" type="number" value={costs} min={0} max={500} onChange={(e) => setCosts(Number(e.target.value))} />
          </div>
          <div className="space-y-1.5">
            <Label className="text-xs">Rebalancing</Label>
            <Select value={rebalance} onValueChange={(v) => setRebalance(v as 'Monthly' | 'Quarterly')} items={[{ value: 'Monthly', label: 'Monthly' }, { value: 'Quarterly', label: 'Quarterly' }]}>
              <SelectTrigger className="w-full" aria-label="Rebalancing"><SelectValue /></SelectTrigger>
              <SelectContent><SelectItem value="Monthly">Monthly</SelectItem><SelectItem value="Quarterly">Quarterly</SelectItem></SelectContent>
            </Select>
          </div>
          <div className="space-y-1.5">
            <Label className="flex items-center gap-1 text-xs">Universe<InfoTip>Intersection: only stocks in both strategies (like-for-like). Union: every strategy may invest in all stocks of either.</InfoTip></Label>
            <Select value={universe} onValueChange={(v) => setUniverse(v as 'Intersection' | 'Union')} items={[{ value: 'Intersection', label: 'Intersection' }, { value: 'Union', label: 'Union' }]}>
              <SelectTrigger className="w-full" aria-label="Universe"><SelectValue /></SelectTrigger>
              <SelectContent><SelectItem value="Intersection">Intersection</SelectItem><SelectItem value="Union">Union</SelectItem></SelectContent>
            </Select>
          </div>
        </div>
        <div className="flex items-center gap-2">
          <Switch id="hybrid" checked={canHybrid && hybrid} onCheckedChange={setHybrid} disabled={!canHybrid} />
          <Label htmlFor="hybrid" className="font-normal">Add hybrid (ML screen + mean-variance)</Label>
          <InfoTip>Only for one mathematical and one ML optimization: a separate, clearly labelled extra strategy. The two strategies themselves stay unchanged.</InfoTip>
        </div>
        <Button className="w-full" onClick={submit} disabled={!strategyA || !strategyB || create.isPending || missingData.checking}>
          {create.isPending || missingData.checking ? <Loader2 className="animate-spin" /> : <Play />} Compare strategies
        </Button>
        {missingData.dialog}
      </CardContent>
    </Card>
  )
}

function ComparisonHistory({ activeId }: { activeId?: string }) {
  const [page, setPage] = useState(1)
  const list = useComparisons(page)
  const navigate = useNavigate()
  return (
    <Card>
      <CardHeader><CardTitle className="text-sm">Saved comparisons</CardTitle></CardHeader>
      <CardContent>
        {list.error ? <ErrorState error={list.error} /> : !list.data ? <LoadingBlock height="h-32" /> : list.data.items.length === 0 ? (
          <p className="text-sm text-muted-foreground">None yet.</p>
        ) : (
          <>
            <ul className="divide-y rounded-md border">
              {list.data.items.map((c) => (
                <li key={c.id}>
                  <button type="button" onClick={() => navigate(`/strategy-comparison/${c.id}`)} aria-current={c.id === activeId ? 'true' : undefined}
                    className={cn('w-full px-3 py-2 text-left text-sm hover:bg-muted', c.id === activeId && 'bg-muted')}>
                    <div className="flex items-center justify-between gap-2">
                      <span className="truncate font-medium">{c.name}</span>
                      <Badge variant="outline" className={c.status === 'Succeeded' ? (c.isFair ? 'border-gain/40 text-gain' : 'border-warn/50 text-warn') : c.status === 'Failed' ? 'border-loss/40 text-loss' : ''}>
                        {c.status === 'Succeeded' ? (c.isFair ? 'Fair' : 'Caveats') : c.status}
                      </Badge>
                    </div>
                    <div className="text-xs text-muted-foreground tabular">{date(c.evaluationStart)} – {date(c.evaluationEnd)} · {relativeTime(c.createdAtUtc)}</div>
                  </button>
                </li>
              ))}
            </ul>
            <Pager page={page} totalPages={list.data.totalPages} onChange={setPage} />
          </>
        )}
      </CardContent>
    </Card>
  )
}

function ComparisonDetail({ id }: { id: string }) {
  const comparison = useComparison(id)
  const remove = useDeleteComparison()
  const navigate = useNavigate()
  if (comparison.error) return <ErrorState error={comparison.error} />
  if (!comparison.data) return <LoadingBlock height="h-96" />
  const c = comparison.data
  const s = c.summary
  const active = s.status === 'Queued' || s.status === 'Running'
  return (
    <div className="space-y-4">
      <Card className="py-4"><CardContent className="space-y-2 px-4">
        <div className="flex flex-wrap items-start justify-between gap-2">
          <div>
            <h2 className="text-lg font-semibold">{s.name}</h2>
            <p className="text-xs text-muted-foreground">
              {date(s.evaluationStart)} – {date(s.evaluationEnd)} · {money(c.configuration.request.initialCapital)} · {c.configuration.request.transactionCostBps} bps ·
              {' '}{c.configuration.request.rebalance} · {c.configuration.request.universe} universe · benchmark {c.benchmarkSymbol}
            </p>
          </div>
          <div className="flex items-center gap-1.5">
            {s.usesSimulatedData && <DataBadge kind="simulated" />}
            <DataBadge kind="backtest" />
            {!active && <Button size="icon-sm" variant="ghost" aria-label="Delete comparison" onClick={async () => {
              await remove.mutateAsync(id)
              navigate('/strategy-comparison')
            }}><Trash2 /></Button>}
          </div>
        </div>
        {active && <div className="space-y-1" aria-live="polite"><Progress value={s.progressPercent} aria-label="Comparison progress" /><p className="text-xs text-muted-foreground">{s.progressMessage}</p></div>}
        {s.status === 'Failed' && <Alert variant="destructive"><AlertTriangle /><AlertTitle>Comparison failed</AlertTitle><AlertDescription>{s.error}</AlertDescription></Alert>}
        <p className="text-[11px] text-muted-foreground">{c.disclaimer}</p>
      </CardContent></Card>
      {c.result && <ComparisonResultView c={c} />}
    </div>
  )
}

function ComparisonResultView({ c }: { c: StrategyComparison }) {
  const r = c.result!
  const strategies = r.strategies
  const colorOf = (s: ComparisonStrategy) => strategyColor[s.key] ?? seriesColor(5)
  const curves = strategies.map((s) => ({ name: s.name, color: colorOf(s), values: s.values.map((v) => v / s.values[0] - 1) }))
  const drawdowns = strategies.map((s) => ({ name: s.name, color: colorOf(s), values: s.drawdowns }))
  const rolling = strategies.map((s) => ({ name: s.name, color: colorOf(s), values: s.rollingSharpe }))
  const traders = strategies.filter((s) => s.kind !== 'Benchmark')
  const allSymbols = [...new Set(traders.flatMap((s) => Object.keys(s.averageWeights)))].sort((a, b) =>
    Math.max(...traders.map((s) => s.averageWeights[b] ?? 0)) - Math.max(...traders.map((s) => s.averageWeights[a] ?? 0))).slice(0, 15)
  const sectors = [...new Set(traders.flatMap((s) => Object.keys(s.averageSectorExposure)))].sort()
  const allocationSeries = traders.map((s) => ({ name: s.name, color: colorOf(s), values: allSymbols.map((k) => s.averageWeights[k] ?? 0) }))
  const sectorSeries = traders.map((s) => ({ name: s.name, color: colorOf(s), values: sectors.map((k) => s.averageSectorExposure[k] ?? 0) }))
  const monthly = monthlySeries(strategies, colorOf)
  return (
    <>
      {r.fairness.isFair ? (
        <Alert className="border-gain/40"><CheckCircle2 className="text-gain" /><AlertTitle>Like-for-like comparison</AlertTitle><AlertDescription>
          <ul className="list-disc pl-4">{r.fairness.sharedConventions.map((x) => <li key={x}>{x}</li>)}</ul>
        </AlertDescription></Alert>
      ) : (
        <Alert variant="destructive"><AlertTriangle /><AlertTitle>Not a fair comparison – no ranking is shown</AlertTitle><AlertDescription>
          <ul className="list-disc pl-4">{r.fairness.issues.map((x) => <li key={x}>{x}</li>)}</ul>
        </AlertDescription></Alert>
      )}
      {r.rankingBySharpe && (
        <p className="text-sm">
          <span className="font-medium">Sharpe ranking over this period: </span>{r.rankingBySharpe.join(' › ')}
          <InfoTip>One historical period. Differences can be due to chance; this is not a statement about future performance.</InfoTip>
        </p>
      )}

      <ChartCard title="Growth of capital (normalized)" legend={curves.map((x) => ({ name: x.name, color: x.color }))} table={timeSeriesTable(r.dates, curves, (v) => pct(v))}
        hint="Cumulative return of every strategy from the same start date and capital.">
        <TimeSeriesChart dates={r.dates} series={curves} format={(v) => pct(v, 0)} zeroLine />
      </ChartCard>

      <Card><CardContent className="py-4">
        <SectionTitle hint="All metrics use the same daily series, risk-free rate and benchmark.">Performance and risk</SectionTitle>
        <PerformanceTable items={strategies.map((s) => s.performance)} colors={strategies.map(colorOf)} />
      </CardContent></Card>

      <ChartCard title="Drawdown" legend={drawdowns.map((x) => ({ name: x.name, color: x.color }))} table={timeSeriesTable(r.dates, drawdowns, (v) => pct(v))}
        hint="Decline from the previous peak of each strategy's value.">
        <TimeSeriesChart dates={r.dates} series={drawdowns} format={(v) => pct(v, 0)} height={280} />
      </ChartCard>

      <ChartCard title={`Rolling Sharpe ratio (${r.rollingSharpeWindow} days)`} legend={rolling.map((x) => ({ name: x.name, color: x.color }))}
        hint="Sharpe ratio over a trailing window; shows how stable relative performance was."
        table={timeSeriesTable(r.dates, rolling.map((x) => ({ ...x, values: x.values.map((v) => v ?? null) })), (v) => v.toFixed(2))}>
        <TimeSeriesChart dates={r.dates} series={rolling.map((x) => ({ ...x, values: x.values.map((v) => v ?? null) }))} format={(v) => v.toFixed(1)} height={220} zeroLine />
      </ChartCard>

      {/* Full width: one group of bars per stock / sector with a bar per strategy needs the room. */}
      <ChartCard title="Average allocation by stock" hint="Average target weight across rebalances (top 15 stocks)." legend={allocationSeries.map((x) => ({ name: x.name, color: x.color }))}
        table={groupedBarTable(allSymbols, allocationSeries, (v) => pct(v, 1))}>
        <GroupedBarChart categories={allSymbols} series={allocationSeries} format={(v) => pct(v, 0)} legend={false} />
      </ChartCard>
      <ChartCard title="Average sector exposure" hint="Average weight per sector across rebalances." legend={sectorSeries.map((x) => ({ name: x.name, color: x.color }))}
        table={groupedBarTable(sectors, sectorSeries, (v) => pct(v, 1))}>
        <GroupedBarChart categories={sectors} series={sectorSeries} format={(v) => pct(v, 0)} legend={false} />
      </ChartCard>

      <div className="grid gap-4 lg:grid-cols-2">
        <ChartCard title="Risk vs return" hint="Annualized volatility (x) against annualized return (y)."
          table={{ columns: ['Strategy', 'Return', 'Volatility'], rows: strategies.map((s) => [s.name, pct(s.performance.annualizedReturn.value), pct(s.performance.annualizedVolatility.value)]) }}>
          <RiskReturnScatter points={strategies.filter((s) => s.performance.annualizedReturn.value !== null && s.performance.annualizedVolatility.value !== null)
            .map((s) => ({ name: s.name, color: colorOf(s), ret: s.performance.annualizedReturn.value!, risk: s.performance.annualizedVolatility.value! }))} height={220} />
        </ChartCard>
        <Card><CardContent className="space-y-3 py-4">
          <SectionTitle hint="Final holdings of strategies A and B, and the average weight overlap Σ min(wᵃ, wᵇ) across rebalances.">Holdings overlap</SectionTitle>
          {r.overlaps.map((o) => (
            <div key={o.strategyA} className="space-y-2 text-sm">
              <div className="grid grid-cols-3 gap-2 tabular">
                <div><div className="text-xs text-muted-foreground">Common holdings</div><div className="font-semibold">{o.commonHoldings.length}</div></div>
                <div><div className="text-xs text-muted-foreground">Final weight overlap</div><div className="font-semibold">{pct(o.weightOverlap, 0)}</div></div>
                <div><div className="text-xs text-muted-foreground">Average weight overlap</div><div className="font-semibold">{pct(o.averageWeightOverlap, 0)}</div></div>
              </div>
              <p className="text-xs"><span className="text-muted-foreground">Both: </span>{o.commonHoldings.join(', ') || '—'}</p>
              <p className="text-xs"><span className="text-muted-foreground">Only {o.strategyA}: </span>{o.onlyA.join(', ') || '—'}</p>
              <p className="text-xs"><span className="text-muted-foreground">Only {o.strategyB}: </span>{o.onlyB.join(', ') || '—'}</p>
            </div>
          ))}
        </CardContent></Card>
      </div>
      <ChartCard title="Turnover" hint="Total one-way turnover over the period as a fraction of capital. Transaction costs are far smaller and listed in the table view and the performance table."
        table={{ columns: ['Strategy', 'Total turnover', 'Costs (% of capital)'], rows: traders.map((s) => [s.name, pct(s.performance.totalTurnover, 1), pct(s.performance.transactionCostsFraction, 2)]) }}>
        <SignedBarChart items={traders.map((s) => ({ name: s.name, value: s.performance.totalTurnover }))} signed={false} format={(v) => pct(v, 0)} />
      </ChartCard>

      <ChartCard title="Monthly returns" hint="Return of each calendar month per strategy (first and last month may be partial). The table view lists every month."
        legend={monthly.series.map((x) => ({ name: x.name, color: x.color }))} table={timeSeriesTable(monthly.dates, monthly.series, (v) => pct(v, 1))}>
        <TimeSeriesChart dates={monthly.dates} series={monthly.series} format={(v) => pct(v, 0)} height={280} zeroLine />
      </ChartCard>

      <Collapsible title="Strategies and methodology" summary={`${strategies.length} strategies`}>
        <div className="space-y-2">
        <ul className="space-y-1.5">{strategies.map((s) => (
          <li key={s.key}><span className="font-medium" style={{ color: 'var(--foreground)' }}>{s.name}: </span><span className="text-muted-foreground">{s.description}</span></li>
        ))}</ul>
        <p className="text-xs text-muted-foreground">{r.methodology}</p>
        <p className="text-xs text-muted-foreground">Universe ({r.universe.length}): {r.universe.join(', ')}</p>
        {c.dataSnapshotHash && <p className="text-xs text-muted-foreground">Data snapshot {c.dataSnapshotHash}. Saved results are never recalculated.</p>}
        </div>
      </Collapsible>
      <Warnings items={[...r.universeNotes, ...r.warnings]} title="Universe and data notes" collapsible />
      <StrategyInsights insights={interpretComparison(c)} guideId="strategy-comparison" title="What the comparison shows" collapsible />
    </>
  )
}

/** Monthly returns of every strategy on one month axis (first day of each month); a missing month stays empty. */
function monthlySeries(strategies: ComparisonStrategy[], colorOf: (s: ComparisonStrategy) => string) {
  const key = (y: number, m: number) => `${y}-${String(m).padStart(2, '0')}-01`
  const dates = [...new Set(strategies.flatMap((s) => s.monthlyReturns.map((m) => key(m.year, m.month))))].sort()
  const series = strategies.map((s) => {
    const byMonth = new Map(s.monthlyReturns.map((m) => [key(m.year, m.month), m.return]))
    return { name: s.name, color: colorOf(s), values: dates.map((d) => byMonth.get(d) ?? null) }
  })
  return { dates, series }
}
