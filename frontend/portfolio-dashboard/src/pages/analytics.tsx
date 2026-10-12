import { useMemo, useState } from 'react'
import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import {
  AllocationDonut, ChartCard, CorrelationHeatmap, FrontierChart, MonthlyReturnsGrid, RiskContributionChart, TimeSeriesChart,
  WeightsOverTimeChart, timeSeriesTable,
} from '@/components/charts'
import { DataBadge, EmptyState, ErrorState, LoadingBlock, MetricCard, MetricValue, PageHeader, Warnings } from '@/components/common'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useHoldings, usePerformance, useProposals, useRisk, useRun, useRuns } from '@/lib/queries'
import { date, money, num, pct } from '@/lib/format'
import { NoPortfolio } from './shared'

const ranges = [
  { key: '1M', months: 1 },
  { key: '3M', months: 3 },
  { key: 'YTD', months: -1 },
  { key: '1Y', months: 12 },
  { key: 'All', months: 0 },
]

function rangeStart(key: string): string | undefined {
  const r = ranges.find((x) => x.key === key)
  if (!r || r.months === 0) return undefined
  const now = new Date()
  if (r.months === -1) return `${now.getUTCFullYear()}-01-01`
  const d = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() - r.months, now.getUTCDate()))
  return d.toISOString().slice(0, 10)
}

export function AnalyticsPage() {
  const { portfolioId, portfolio, isLoading } = usePortfolioContext()
  const [range, setRange] = useState('All')
  const perf = usePerformance(portfolioId, rangeStart(range))
  const risk = useRisk(portfolioId)
  const holdings = useHoldings(portfolioId)
  const runs = useRuns(portfolioId)
  const latestOk = runs.data?.items.find((r) => r.status === 'Succeeded')
  const run = useRun(latestOk?.id)
  const proposals = useProposals(portfolioId)
  const ccy = portfolio?.baseCurrency ?? 'USD'

  const weightHistory = useMemo(() => (proposals.data?.items ?? [])
    .filter((p) => p.status === 'Executed')
    .sort((a, b) => a.createdAtUtc.localeCompare(b.createdAtUtc))
    .map((p) => ({ date: p.createdAtUtc.slice(0, 10), weights: Object.fromEntries(p.lines.filter((l) => l.resultingWeight > 0).map((l) => [l.symbol, l.resultingWeight])) })),
  [proposals.data])

  if (!isLoading && !portfolioId) return <NoPortfolio />
  const p = perf.data
  const hasSeries = !!p && p.series.length > 1

  return (
    <>
      <PageHeader
        title="Portfolio analytics"
        description="Realized performance of the simulated portfolio and ex-ante risk of its current holdings."
        actions={
          <div className="flex items-center gap-1 rounded-lg border p-0.5" role="group" aria-label="Date range">
            {ranges.map((r) => (
              <Button key={r.key} size="sm" variant={range === r.key ? 'secondary' : 'ghost'} aria-pressed={range === r.key} onClick={() => setRange(r.key)}>{r.key}</Button>
            ))}
          </div>
        }
      />
      {perf.error && <ErrorState error={perf.error} />}

      <div className="grid grid-cols-2 gap-3 md:grid-cols-6">
        <MetricCard label="Total return" loading={perf.isLoading} value={<MetricValue metric={p?.totalReturn} format={(v) => pct(v, 2, true)} />} sub={p?.from ? `since ${date(p.from)}` : undefined} hint="Time-weighted cumulative return over the selected range." />
        <MetricCard label="Annualized return" loading={perf.isLoading} value={<MetricValue metric={p?.annualizedReturn} format={(v) => pct(v)} />} hint="Geometric (CAGR), requires at least 20 daily returns." />
        <MetricCard label="Volatility" loading={perf.isLoading} value={<MetricValue metric={p?.annualizedVolatility} format={(v) => pct(v)} />} hint="Sample standard deviation of daily returns × √252." />
        <MetricCard label="Sharpe" loading={perf.isLoading} value={<MetricValue metric={p?.sharpeRatio} format={(v) => num(v)} />} hint="(Mean daily return − rf/252) / σ × √252." />
        <MetricCard label="Sortino" loading={perf.isLoading} value={<MetricValue metric={p?.sortinoRatio} format={(v) => num(v)} />} hint="Like Sharpe, but divides by downside deviation below the daily risk-free rate." />
        <MetricCard label="Max drawdown" loading={perf.isLoading} value={<MetricValue metric={p?.maxDrawdown} format={(v) => pct(v)} />} hint="Largest peak-to-trough decline of the value index." />
      </div>
      <div className="mt-3 flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
        {p?.usesSimulatedData ? <DataBadge kind="simulated" /> : p && <DataBadge kind="cached" label={p.priceSource} />}
        <span>{p?.methodology}</span>
      </div>
      <div className="mt-3"><Warnings items={p?.warnings} /></div>

      <Tabs defaultValue="performance" className="mt-6">
        <TabsList className="flex-wrap">
          <TabsTrigger value="performance">Performance</TabsTrigger>
          <TabsTrigger value="risk">Risk</TabsTrigger>
          <TabsTrigger value="allocation">Allocation</TabsTrigger>
          <TabsTrigger value="frontier">Efficient frontier</TabsTrigger>
        </TabsList>

        <TabsContent value="performance" className="mt-4 space-y-4">
          {perf.isLoading ? <LoadingBlock height="h-80" /> : !hasSeries ? (
            <EmptyState title="Not enough history yet"
              description="Charts appear once the portfolio has at least two daily closes after its first simulated execution. Use the Backtest page for long historical simulations."
              action={<Button variant="outline" render={<Link to="/backtest" />}>Open backtesting</Button>} />
          ) : (
            <>
              <div className="grid gap-4 lg:grid-cols-2">
                <ChartCard title="Portfolio value" description={`${ccy}, daily close, ${date(p!.from)} – ${date(p!.to)}`}
                  table={timeSeriesTable(p!.series.map((x) => x.date), [{ name: 'Value', values: p!.series.map((x) => x.value) }], (v) => money(v, ccy))}>
                  <TimeSeriesChart dates={p!.series.map((x) => x.date)} series={[{ name: 'Portfolio value', values: p!.series.map((x) => x.value) }]} format={(v) => money(v, ccy, { compact: true })} area />
                </ChartCard>
                <ChartCard title={`Cumulative return vs ${p!.benchmarkSymbol}`} description="Time-weighted, %"
                  legend={[{ name: 'Portfolio', color: 'var(--series-1)' }, { name: p!.benchmarkSymbol, color: 'var(--series-2)', dashed: true }]}
                  table={timeSeriesTable(p!.series.map((x) => x.date), [{ name: 'Portfolio', values: p!.series.map((x) => x.cumulativeReturn) }, { name: p!.benchmarkSymbol, values: p!.series.map((x) => x.benchmarkCumulativeReturn) }], (v) => pct(v))}>
                  <TimeSeriesChart dates={p!.series.map((x) => x.date)} zeroLine format={(v) => pct(v, 1)}
                    series={[{ name: 'Portfolio', values: p!.series.map((x) => x.cumulativeReturn) }, { name: p!.benchmarkSymbol, values: p!.series.map((x) => x.benchmarkCumulativeReturn), dashed: true }]} />
                </ChartCard>
              </div>
              <ChartCard title="Drawdown" description="Decline from running peak, %"
                table={timeSeriesTable(p!.series.map((x) => x.date), [{ name: 'Drawdown', values: p!.series.map((x) => x.drawdown) }], (v) => pct(v))}>
                <TimeSeriesChart dates={p!.series.map((x) => x.date)} series={[{ name: 'Drawdown', values: p!.series.map((x) => x.drawdown), color: 'var(--series-8)' }]} format={(v) => pct(v, 0)} area height={200} yDomain={['dataMin', 0]} />
              </ChartCard>
              <Card>
                <CardHeader><CardTitle className="text-sm">Monthly returns</CardTitle></CardHeader>
                <CardContent><MonthlyReturnsGrid items={p!.monthlyReturns.map((m) => ({ year: m.year, month: m.month, value: m.portfolio ?? null }))} /></CardContent>
              </Card>
            </>
          )}
        </TabsContent>

        <TabsContent value="risk" className="mt-4 space-y-4">
          {risk.isLoading ? <LoadingBlock /> : risk.data && (
            <>
              <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
                <MetricCard label="Volatility (ex-ante)" value={<MetricValue metric={risk.data.annualizedVolatility} format={(v) => pct(v)} />} sub={`${risk.data.observations} days`} />
                <MetricCard label={`1-day VaR ${pct(risk.data.confidenceLevel, 0)}`} value={<MetricValue metric={risk.data.valueAtRisk} format={(v) => pct(v)} />}
                  sub={risk.data.valueAtRisk.value !== null && holdings.data ? `≈ ${money(risk.data.valueAtRisk.value * holdings.data.summary.totalValue, ccy, { decimals: 0 })}` : undefined}
                  hint="Historical one-day loss not exceeded with the stated confidence, using the current weights on past returns." />
                <MetricCard label={`1-day CVaR ${pct(risk.data.confidenceLevel, 0)}`} value={<MetricValue metric={risk.data.conditionalValueAtRisk} format={(v) => pct(v)} />} hint="Expected shortfall: average loss on the days beyond the VaR threshold." />
                <MetricCard label="Beta vs benchmark" value={<MetricValue metric={risk.data.beta} format={(v) => num(v)} />} hint="Covariance with benchmark returns / benchmark variance." />
                <MetricCard label="Tracking error" value={<MetricValue metric={risk.data.trackingError} format={(v) => pct(v)} />} hint="Annualized volatility of the return difference to the benchmark." />
                <MetricCard label="Information ratio" value={<MetricValue metric={risk.data.informationRatio} format={(v) => num(v)} />} hint="Annualized active return / tracking error." />
                <MetricCard label="Sharpe (ex-ante)" value={<MetricValue metric={risk.data.sharpeRatio} format={(v) => num(v)} />} />
                <MetricCard label="Max drawdown (ex-ante)" value={<MetricValue metric={risk.data.maxDrawdown} format={(v) => pct(v)} />} />
              </div>
              <p className="text-xs text-muted-foreground">{risk.data.methodology} Window {date(risk.data.from)} – {date(risk.data.to)}.</p>
              <Warnings items={risk.data.warnings} />
              {risk.data.contributions.length > 0 && (
                <div className="grid gap-4 lg:grid-cols-2">
                  <ChartCard title="Risk contribution by holding" description="Weight vs share of portfolio variance"
                    legend={[{ name: 'Weight', color: 'var(--series-1)' }, { name: 'Risk contribution', color: 'var(--series-2)' }]}
                    table={{ columns: ['Symbol', 'Weight', 'Risk contribution', 'Volatility'], rows: risk.data.contributions.map((c) => [c.symbol, pct(c.weight), pct(c.riskContribution), pct(c.volatility)]) }}>
                    <RiskContributionChart items={risk.data.contributions.map((c) => ({ symbol: c.symbol, weight: c.weight, risk: c.riskContribution }))} />
                  </ChartCard>
                  <ChartCard title="Correlation of current holdings" description={`Daily returns, ${date(risk.data.from)} – ${date(risk.data.to)}`}>
                    <CorrelationHeatmap symbols={risk.data.correlationSymbols} matrix={risk.data.correlation} />
                  </ChartCard>
                </div>
              )}
            </>
          )}
        </TabsContent>

        <TabsContent value="allocation" className="mt-4 grid gap-4 lg:grid-cols-2">
          <Card>
            <CardHeader><CardTitle className="text-sm">Current allocation</CardTitle></CardHeader>
            <CardContent>
              {holdings.data ? (
                <AllocationDonut items={[
                  ...holdings.data.holdings.filter((h) => (h.weight ?? 0) > 0).map((h) => ({ name: h.symbol, value: h.weight! })),
                  { name: 'Cash', value: holdings.data.summary.totalValue > 0 ? holdings.data.summary.cashBalance / holdings.data.summary.totalValue : 1 },
                ]} />
              ) : <LoadingBlock />}
            </CardContent>
          </Card>
          <Card>
            <CardHeader><CardTitle className="text-sm">Historical asset weights</CardTitle></CardHeader>
            <CardContent>
              {weightHistory.length === 0 ? <p className="text-sm text-muted-foreground">Appears after the first simulated execution. Weights are recorded at each execution.</p>
                : weightHistory.length === 1 ? <p className="text-sm text-muted-foreground">One execution so far ({weightHistory[0].date}); history builds up with each rebalance.</p>
                : <WeightsOverTimeChart points={weightHistory} />}
            </CardContent>
          </Card>
        </TabsContent>

        <TabsContent value="frontier" className="mt-4">
          {run.data ? (
            <ChartCard title="Efficient frontier (latest successful run)" description={`Estimation window ${date(run.data.periodStart)} – ${date(run.data.periodEnd)}`}
              legend={[{ name: 'Efficient frontier', color: 'var(--series-1)' }, { name: 'Selected portfolio', color: 'var(--series-2)' }, { name: 'Reference portfolios', color: 'var(--series-3)' }, { name: 'Individual assets', color: 'var(--chart-axis)' }]}
              actions={<Button variant="link" size="sm" render={<Link to={`/optimize/${run.data.id}`} />}>Open run</Button>}>
              <FrontierChart frontier={run.data.analytics.frontier}
                assets={run.data.analytics.assetStatistics.map((s) => ({ symbol: s.symbol, volatility: s.volatility, expectedReturn: s.expectedReturn }))}
                selected={{ volatility: run.data.volatility!, expectedReturn: run.data.expectedReturn!, sharpeRatio: run.data.sharpeRatio }}
                references={run.data.analytics.references} />
            </ChartCard>
          ) : <EmptyState title="No successful optimization run" action={<Button render={<Link to="/optimize" />}>Optimize</Button>} />}
        </TabsContent>
      </Tabs>
    </>
  )
}
