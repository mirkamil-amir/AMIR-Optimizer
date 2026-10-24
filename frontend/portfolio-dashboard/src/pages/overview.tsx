import { useMemo, useState } from 'react'
import { Link, useSearchParams } from 'react-router'
import { ArrowRight, CircleCheck, Sigma } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import {
  AllocationDonut, ChartCard, CorrelationHeatmap, FrontierChart, MonthlyReturnsGrid, RiskContributionChart, TimeSeriesChart,
  WeightsOverTimeChart, timeSeriesTable,
} from '@/components/charts'
import { DataBadge, EmptyState, ErrorState, LoadingBlock, MetricCard, MetricValue, ResultSection, Signed, Warnings } from '@/components/common'
import { ValuationProblems } from '@/components/currency'
import { portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { useHoldings, useMarketSummary, usePerformance, useProposals, useRisk, useRun, useRuns, useTransactions } from '@/lib/queries'
import { date, dateTime, money, num, objectiveLabels, pct, qty, relativeTime } from '@/lib/format'
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

/**
 * Overview of one portfolio – everything about its state and recorded history on one page (it replaced the separate
 * Performance section on 2026-10-24): key figures, portfolio vs benchmark with the efficient frontier of the latest
 * successful Math Optimization right below, then recorded performance, ex-ante risk and allocation over time. Every chart
 * appears once. Recorded history is not a backtest.
 */
export function OverviewPage() {
  const { portfolioId, portfolio, isLoading } = usePortfolioContext()
  const [params] = useSearchParams()
  const created = params.get('created') === '1'
  const [range, setRange] = useState('All')
  const holdings = useHoldings(portfolioId)
  const perf = usePerformance(portfolioId, rangeStart(range))
  const risk = useRisk(portfolioId)
  const runs = useRuns(portfolioId)
  const latestOk = runs.data?.items.find((r) => r.status === 'Succeeded')
  const run = useRun(latestOk?.id)
  const proposals = useProposals(portfolioId)
  const tx = useTransactions(portfolioId, 1, undefined, 6)
  const status = useMarketSummary()

  const weightHistory = useMemo(() => (proposals.data?.items ?? [])
    .filter((p) => p.status === 'Executed')
    .sort((a, b) => a.createdAtUtc.localeCompare(b.createdAtUtc))
    .map((p) => ({ date: p.createdAtUtc.slice(0, 10), weights: Object.fromEntries(p.lines.filter((l) => l.resultingWeight > 0).map((l) => [l.symbol, l.resultingWeight])) })),
  [proposals.data])

  if (!isLoading && !portfolioId) return <NoPortfolio />
  const id = portfolioId ?? ''
  const s = holdings.data?.summary
  const ccy = portfolio?.baseCurrency ?? 'USD'
  const hasHoldings = (holdings.data?.holdings ?? []).some((h) => h.quantity > 0)
  const p = perf.data
  const hasSeries = !!p && p.series.length > 1
  const dates = p?.series.map((x) => x.date) ?? []

  return (
    <>
      <div className="mb-4 flex flex-wrap items-center justify-between gap-2">
        <h2 className="heading-display text-[1.3rem]">Overview</h2>
        <div className="flex flex-wrap items-center gap-2">
          {s?.usesSimulatedData && <DataBadge kind="simulated" />}
          <div className="flex items-center gap-1 rounded-lg border bg-background p-0.5" role="group" aria-label="Date range">
            {ranges.map((r) => (
              <Button key={r.key} size="sm" variant={range === r.key ? 'secondary' : 'ghost'} aria-pressed={range === r.key} onClick={() => setRange(r.key)}>{r.key}</Button>
            ))}
          </div>
          <Button render={<Link to={portfolioPath(id, 'optimize')} />}><Sigma /> Optimize</Button>
        </div>
      </div>
      {created && <NextSteps id={id} />}

      {holdings.error && <ErrorState error={holdings.error} onRetry={() => holdings.refetch()} />}
      {perf.error && <ErrorState error={perf.error} />}

      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        <MetricCard label="Total value" loading={!s} value={s?.totalValue === null ? 'Not available' : money(s?.totalValue, ccy)} sub={`Net contributions ${money(s?.netContributions, ccy)}`}
          hint="Market value of all simulated holdings at the latest price (converted into the portfolio currency at the rate of the price date) plus cash." />
        <MetricCard label="Cash · invested" loading={!s} value={money(s?.cashBalance, ccy)} sub={`${pct(s?.investedFraction, 1)} invested (${money(s?.marketValue, ccy, { compact: true })})`} hint="Uninvested simulated cash and the share of total value held in equities." />
        <MetricCard label="Total return" loading={!s} tone="signed" numeric={s?.totalReturn} value={pct(s?.totalReturn, 2, true)}
          sub={s?.benchmarkReturn !== null && s?.benchmarkReturn !== undefined ? <>vs {s.benchmarkSymbol} {pct(s.benchmarkReturn, 2, true)}</> : 'Since first contribution'}
          hint="(Total value − net contributions) / net contributions. Includes fees and realized P&L; dividends are not credited to the paper portfolio." />
        <MetricCard label="Daily change" loading={!s} tone="signed" numeric={s?.dailyChange} value={money(s?.dailyChange, ccy, { signed: true })}
          sub={s?.dailyChangePercent !== null && s?.dailyChangePercent !== undefined ? pct(s.dailyChangePercent, 2, true) : 'No prior close'}
          hint="Change of held positions between the last two daily closes. Shares bought at the last close are excluded." />
        <MetricCard label="Volatility (ex-ante)" loading={risk.isLoading} value={<MetricValue metric={risk.data?.annualizedVolatility} format={(v) => pct(v)} />}
          sub="Current weights · trailing 1y" hint="Annualized standard deviation of daily returns of the current weights applied to the last 252 trading days (historical simulation)." />
        <MetricCard label="Sharpe ratio (ex-ante)" loading={risk.isLoading} value={<MetricValue metric={risk.data?.sharpeRatio} format={(v) => num(v)} />}
          sub="Same window, vs risk-free rate" hint="(Annualized mean daily return − risk-free rate) / annualized volatility of the current weights over the trailing window." />
        <MetricCard label="Max drawdown (ex-ante)" loading={risk.isLoading}
          value={<MetricValue metric={risk.data?.maxDrawdown} format={(v) => pct(v)} />} sub="Current weights · trailing 1y" hint="Largest peak-to-trough decline of the current weights over the trailing window." />
        <MetricCard label="Unrealized P&L" loading={!s} tone="signed" numeric={s?.unrealizedPnl} value={money(s?.unrealizedPnl, ccy, { signed: true })}
          sub={`Realized ${money(s?.realizedPnl, ccy, { signed: true })} · fees ${money(s?.totalFees, ccy)}`} hint="Market value − weighted-average cost basis (buy commissions are included in cost)." />
      </div>

      <div className="mt-4 space-y-3">
        <ValuationProblems problems={s?.valuationProblems} />
        <Warnings items={s?.warnings.filter((w) => !s.valuationProblems?.includes(w))} />
      </div>

      <div className="mt-6 grid gap-4 lg:grid-cols-3">
        <div className="lg:col-span-2">
          {perf.isLoading ? <LoadingBlock height="h-80" /> : hasSeries ? (
            <ChartCard
              title="Portfolio vs benchmark"
              description={`Cumulative time-weighted return, ${date(p!.from)} – ${date(p!.to)}`}
              hint={p!.methodology}
              legend={[{ name: 'Portfolio', color: 'var(--series-1)' }, { name: p!.benchmarkSymbol, color: 'var(--series-2)', dashed: true }]}
              table={timeSeriesTable(dates, [
                { name: 'Portfolio', values: p!.series.map((x) => x.cumulativeReturn) },
                { name: p!.benchmarkSymbol, values: p!.series.map((x) => x.benchmarkCumulativeReturn) },
              ], (v) => pct(v))}
            >
              <TimeSeriesChart
                dates={dates}
                series={[
                  { name: 'Portfolio', values: p!.series.map((x) => x.cumulativeReturn) },
                  { name: p!.benchmarkSymbol, values: p!.series.map((x) => x.benchmarkCumulativeReturn), dashed: true },
                ]}
                format={(v) => pct(v, 1)}
                zeroLine
              />
            </ChartCard>
          ) : (
            <Card className="h-full">
              <CardHeader><CardTitle className="text-sm">Portfolio vs benchmark</CardTitle></CardHeader>
              <CardContent>
                <EmptyState
                  title={hasHoldings ? 'Performance history is just starting' : 'No simulated trades yet'}
                  description={hasHoldings
                    ? 'Realized performance accumulates with each new daily close after your first simulated execution. Meanwhile, see the in-sample comparison in the optimizer or run a backtest.'
                    : 'Optimize the portfolio and confirm the simulated purchases, or record your own transactions, to start tracking performance.'}
                  action={<Button variant="outline" render={<Link to={portfolioPath(id, hasHoldings ? 'backtest' : 'optimize')} />}>{hasHoldings ? 'Run a backtest' : 'Go to optimizer'} <ArrowRight /></Button>}
                />
              </CardContent>
            </Card>
          )}
        </div>

        <Card>
          <CardHeader><CardTitle className="text-sm">Allocation</CardTitle></CardHeader>
          <CardContent>
            {!holdings.data ? <LoadingBlock /> : (
              <AllocationDonut
                items={[
                  ...holdings.data.holdings.filter((h) => (h.weight ?? 0) > 0).map((h) => ({ name: h.symbol, value: h.weight! })),
                  { name: 'Cash', value: (s?.totalValue ?? 0) > 0 ? s!.cashBalance / s!.totalValue! : 1 },
                ]}
                height={180}
                centerLabel={<><span className="text-xs text-muted-foreground">Invested</span><span className="text-lg font-semibold">{pct(s?.investedFraction, 0)}</span></>}
              />
            )}
          </CardContent>
        </Card>
      </div>

      <div className="mt-4">
        {runs.isLoading || (latestOk && run.isLoading) ? <LoadingBlock height="h-80" /> : run.data ? (
          <ChartCard title="Efficient frontier (latest successful Math Optimization)"
            description={`${objectiveLabels[run.data.objective] ?? run.data.objective} · estimation window ${date(run.data.periodStart)} – ${date(run.data.periodEnd)} · in-sample estimates, not forecasts`}
            legend={[{ name: 'Efficient frontier', color: 'var(--series-1)' }, { name: 'Selected portfolio', color: 'var(--series-2)' }, { name: 'Reference portfolios', color: 'var(--series-3)' }, { name: 'Individual assets', color: 'var(--chart-axis)' }]}
            actions={<Button variant="link" size="sm" render={<Link to={portfolioPath(id, `optimize/${run.data.id}`)} />}>Open run</Button>}>
            <FrontierChart frontier={run.data.analytics.frontier}
              assets={run.data.analytics.assetStatistics.map((a) => ({ symbol: a.symbol, volatility: a.volatility, expectedReturn: a.expectedReturn }))}
              selected={{ volatility: run.data.volatility!, expectedReturn: run.data.expectedReturn!, sharpeRatio: run.data.sharpeRatio }}
              references={run.data.analytics.references} />
          </ChartCard>
        ) : (
          <Card>
            <CardHeader><CardTitle className="text-sm">Efficient frontier</CardTitle></CardHeader>
            <CardContent>
              <EmptyState title="No successful Math Optimization yet" description="The frontier of the latest successful Math Optimization of this portfolio appears here."
                action={<Button render={<Link to={portfolioPath(id, 'optimize')} />}><Sigma /> Optimize</Button>} />
            </CardContent>
          </Card>
        )}
      </div>

      <div className="mt-8 space-y-8">
        <ResultSection title="Recorded performance" hint="Recorded history of this portfolio – value, cash and time-weighted return since its first trade, for the date range above. Not a backtest.">
          <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
            <MetricCard label="Time-weighted return" loading={perf.isLoading} value={<MetricValue metric={p?.totalReturn} format={(v) => pct(v, 2, true)} />} sub={p?.from ? `since ${date(p.from)}` : undefined} hint="Time-weighted cumulative return over the selected range; deposits and withdrawals do not count as returns." />
            <MetricCard label="Annualized return" loading={perf.isLoading} value={<MetricValue metric={p?.annualizedReturn} format={(v) => pct(v)} />} hint="Geometric (CAGR), requires at least 20 daily returns." />
            <MetricCard label="Volatility (realized)" loading={perf.isLoading} value={<MetricValue metric={p?.annualizedVolatility} format={(v) => pct(v)} />} hint="Sample standard deviation of daily returns × √252." />
            <MetricCard label="Sharpe (realized)" loading={perf.isLoading} value={<MetricValue metric={p?.sharpeRatio} format={(v) => num(v)} />} hint="(Mean daily return − rf/252) / σ × √252." />
            <MetricCard label="Sortino" loading={perf.isLoading} value={<MetricValue metric={p?.sortinoRatio} format={(v) => num(v)} />} hint="Like Sharpe, but divides by downside deviation below the daily risk-free rate." />
            <MetricCard label="Max drawdown (realized)" loading={perf.isLoading} value={<MetricValue metric={p?.maxDrawdown} format={(v) => pct(v)} />} hint="Largest peak-to-trough decline of the value index." />
          </div>
          <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
            {p?.usesSimulatedData ? <DataBadge kind="simulated" /> : p && <DataBadge kind="cached" label="Stored prices" />}
            <span>{p?.methodology}</span>
          </div>
          <Warnings items={p?.warnings} />
          {perf.isLoading ? <LoadingBlock height="h-64" /> : !hasSeries ? (
            <p className="rounded-lg border border-dashed px-4 py-6 text-center text-sm text-muted-foreground">
              Charts of value, drawdown and monthly returns appear once the portfolio has at least two daily closes after its first simulated execution.
            </p>
          ) : (
            <>
              <div className="grid gap-4 lg:grid-cols-2">
                <ChartCard title="Portfolio value and cash" description={`${ccy}, daily close, ${date(p!.from)} – ${date(p!.to)}. Invested = value − cash. Deposits and withdrawals move the value but are not returns.`}
                  legend={[{ name: 'Total value', color: 'var(--series-1)' }, { name: 'Cash', color: 'var(--series-2)', dashed: true }]}
                  table={timeSeriesTable(dates, [
                    { name: 'Total value', values: p!.series.map((x) => x.value) },
                    { name: 'Cash', values: p!.series.map((x) => x.cash) },
                  ], (v) => money(v, ccy))}>
                  <TimeSeriesChart dates={dates} format={(v) => money(v, ccy, { compact: true })}
                    series={[{ name: 'Total value', values: p!.series.map((x) => x.value) }, { name: 'Cash', values: p!.series.map((x) => x.cash), dashed: true }]} />
                </ChartCard>
                <ChartCard title="Drawdown" description="Decline from running peak, %"
                  table={timeSeriesTable(dates, [{ name: 'Drawdown', values: p!.series.map((x) => x.drawdown) }], (v) => pct(v))}>
                  <TimeSeriesChart dates={dates} series={[{ name: 'Drawdown', values: p!.series.map((x) => x.drawdown), color: 'var(--series-8)' }]} format={(v) => pct(v, 0)} area yDomain={['dataMin', 0]} />
                </ChartCard>
              </div>
              <Card>
                <CardHeader><CardTitle className="text-sm">Monthly returns</CardTitle></CardHeader>
                <CardContent><MonthlyReturnsGrid items={p!.monthlyReturns.map((m) => ({ year: m.year, month: m.month, value: m.portfolio ?? null }))} /></CardContent>
              </Card>
            </>
          )}
        </ResultSection>

        <ResultSection title="Risk of the current holdings" hint="Ex-ante: today's weights applied to past daily returns (historical simulation). Volatility, Sharpe ratio and maximum drawdown of the same window are in the key figures above.">
          {risk.isLoading ? <LoadingBlock /> : risk.data && (
            <>
              <div className="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
                <MetricCard label={`1-day VaR ${pct(risk.data.confidenceLevel, 0)}`} value={<MetricValue metric={risk.data.valueAtRisk} format={(v) => pct(v)} />}
                  sub={risk.data.valueAtRisk.value !== null && s?.totalValue != null ? `≈ ${money(risk.data.valueAtRisk.value * s.totalValue, ccy, { decimals: 0 })}` : undefined}
                  hint="Historical one-day loss not exceeded with the stated confidence, using the current weights on past returns." />
                <MetricCard label={`1-day CVaR ${pct(risk.data.confidenceLevel, 0)}`} value={<MetricValue metric={risk.data.conditionalValueAtRisk} format={(v) => pct(v)} />} hint="Expected shortfall: average loss on the days beyond the VaR threshold." />
                <MetricCard label="Beta vs benchmark" value={<MetricValue metric={risk.data.beta} format={(v) => num(v)} />} hint="Covariance with benchmark returns / benchmark variance." />
                <MetricCard label="Tracking error" value={<MetricValue metric={risk.data.trackingError} format={(v) => pct(v)} />} hint="Annualized volatility of the return difference to the benchmark." />
                <MetricCard label="Information ratio" value={<MetricValue metric={risk.data.informationRatio} format={(v) => num(v)} />} hint="Annualized active return / tracking error." />
              </div>
              <p className="text-xs text-muted-foreground">{risk.data.methodology} Window {date(risk.data.from)} – {date(risk.data.to)} ({risk.data.observations} days).</p>
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
        </ResultSection>

        <ResultSection title="Allocation over time" hint="Weights recorded at each confirmed execution; the current allocation is shown next to Portfolio vs benchmark.">
          <Card>
            <CardHeader><CardTitle className="text-sm">Historical asset weights</CardTitle></CardHeader>
            <CardContent>
              {weightHistory.length === 0 ? <p className="text-sm text-muted-foreground">Appears after the first simulated execution. Weights are recorded at each execution.</p>
                : weightHistory.length === 1 ? <p className="text-sm text-muted-foreground">One execution so far ({weightHistory[0].date}); history builds up with each rebalance.</p>
                : <WeightsOverTimeChart points={weightHistory} />}
            </CardContent>
          </Card>
        </ResultSection>
      </div>

      <div className="mt-8 grid gap-4 lg:grid-cols-3">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle className="text-sm">Recent optimization runs</CardTitle>
            <Button variant="link" size="sm" render={<Link to={portfolioPath(id, 'runs')} />}>All</Button>
          </CardHeader>
          <CardContent className="space-y-2">
            {runs.data?.items.length === 0 && <p className="text-sm text-muted-foreground">No runs yet.</p>}
            {runs.data?.items.slice(0, 5).map((r) => (
              <Link key={r.id} to={portfolioPath(id, `optimize/${r.id}`)} className="block rounded-md border px-3 py-2 text-sm hover:bg-muted/60">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium">{objectiveLabels[r.objective]}</span>
                  <Badge variant={r.status === 'Succeeded' ? 'secondary' : 'destructive'}>{r.status}</Badge>
                </div>
                <div className="mt-0.5 flex justify-between text-xs text-muted-foreground tabular">
                  <span>{dateTime(r.createdAtUtc)}</span>
                  {r.status === 'Succeeded' && <span>E[r] {pct(r.expectedReturn, 1)} · σ {pct(r.volatility, 1)} · SR {num(r.sharpeRatio)}</span>}
                </div>
              </Link>
            ))}
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle className="text-sm">Recent simulated transactions</CardTitle>
            <Button variant="link" size="sm" render={<Link to={portfolioPath(id, 'transactions')} />}>Ledger</Button>
          </CardHeader>
          <CardContent>
            {tx.data?.items.length === 0 && <p className="text-sm text-muted-foreground">No executions yet.</p>}
            <ul className="divide-y text-sm">
              {tx.data?.items.map((t) => (
                <li key={t.id} className="flex items-center justify-between py-1.5">
                  <span className="flex items-center gap-2">
                    <Badge variant="outline" className={t.side === 'Buy' ? 'text-gain' : 'text-loss'}>{t.side}</Badge>
                    <span className="font-medium">{t.symbol}</span>
                    <span className="tabular text-muted-foreground">{qty(t.quantity)} @ {money(t.price, t.currency)}</span>
                  </span>
                  <Signed value={t.netCashFlow}>{money(t.netCashFlow, ccy, { signed: true })}</Signed>
                </li>
              ))}
            </ul>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle className="text-sm">Data freshness</CardTitle>
            <Button variant="link" size="sm" render={<Link to="/market-data" />}>Details</Button>
          </CardHeader>
          <CardContent className="space-y-2 text-sm">
            {status.data && (
              <>
                {status.data.activeProviderIsSimulated && (
                  <div className="flex items-center justify-between">
                    <span>Prices</span>
                    <DataBadge kind="simulated" />
                  </div>
                )}
                {(['Fresh', 'Stale', 'Missing'] as const).map((f) => {
                  const n = { Fresh: status.data.fresh, Stale: status.data.stale, Missing: status.data.missing }[f]
                  return (
                    <div key={f} className="flex items-center justify-between">
                      <span className="text-muted-foreground">{f}</span>
                      <span className="tabular">{n} asset{n === 1 ? '' : 's'}</span>
                    </div>
                  )
                })}
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Last download</span>
                  <span>{relativeTime(status.data.lastRetrievedAtUtc)}</span>
                </div>
              </>
            )}
          </CardContent>
        </Card>
      </div>
    </>
  )
}

/** Shown once after Portfolio Creation: what to do next with the new (still cash-only) portfolio. */
function NextSteps({ id }: { id: string }) {
  return (
    <section aria-label="Next steps" className="mb-6 rounded-xl border border-primary/30 bg-primary/5 p-4">
      <h3 className="flex items-center gap-2 text-sm font-semibold"><CircleCheck className="size-4 text-gain" /> Portfolio created – it holds only simulated cash so far</h3>
      <ol className="mt-2 list-decimal space-y-1 pl-5 text-sm text-muted-foreground">
        <li><Link className="underline underline-offset-4" to={portfolioPath(id, 'optimize')}>Run an optimization</Link> on the stocks you chose, then review and confirm the simulated purchases.</li>
        <li>Or record your own buys, deposits and dividends with <span className="font-medium text-foreground">Record transaction</span> above.</li>
        <li>Follow its value, cash, returns and risk on this Overview once the first purchases are executed.</li>
      </ol>
    </section>
  )
}
