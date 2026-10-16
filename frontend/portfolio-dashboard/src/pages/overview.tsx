import { Link } from 'react-router'
import { ArrowRight, Sigma } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { AllocationDonut, ChartCard, TimeSeriesChart, timeSeriesTable } from '@/components/charts'
import { DataBadge, EmptyState, ErrorState, LoadingBlock, MetricCard, MetricValue, PageHeader, Signed, Warnings } from '@/components/common'
import { ValuationProblems } from '@/components/currency'
import { CurrencyConverterCard } from '@/components/currency-converter'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useHoldings, useMarketStatus, usePerformance, useRisk, useRuns, useTransactions } from '@/lib/queries'
import { date, dateTime, money, num, objectiveLabels, pct, qty, relativeTime } from '@/lib/format'
import { NoPortfolio } from './shared'

export function OverviewPage() {
  const { portfolioId, portfolio, isLoading } = usePortfolioContext()
  const holdings = useHoldings(portfolioId)
  const perf = usePerformance(portfolioId)
  const risk = useRisk(portfolioId)
  const runs = useRuns(portfolioId)
  const tx = useTransactions(portfolioId, 1, undefined, 6)
  const status = useMarketStatus()

  if (!isLoading && !portfolioId) return <NoPortfolio />
  const s = holdings.data?.summary
  const ccy = portfolio?.baseCurrency ?? 'USD'
  const hasHoldings = (holdings.data?.holdings ?? []).some((h) => h.quantity > 0)

  return (
    <>
      <PageHeader
        title={portfolio?.name ?? 'Overview'}
        description={s?.valuationDate ? `Valued at stored prices as of ${date(s.valuationDate)}` : 'Simulated portfolio overview'}
        actions={
          <>
            {s?.usesSimulatedData && <DataBadge kind="simulated" />}
            <Button render={<Link to="/optimize" />}>
              <Sigma /> Optimize
            </Button>
          </>
        }
      />

      {holdings.error && <ErrorState error={holdings.error} onRetry={() => holdings.refetch()} />}

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
          {perf.isLoading ? <LoadingBlock height="h-80" /> : perf.data && perf.data.series.length > 1 ? (
            <ChartCard
              title="Portfolio vs benchmark"
              description={`Cumulative time-weighted return, ${date(perf.data.from)} – ${date(perf.data.to)}`}
              hint={perf.data.methodology}
              legend={[{ name: 'Portfolio', color: 'var(--series-1)' }, { name: perf.data.benchmarkSymbol, color: 'var(--series-2)', dashed: true }]}
              table={timeSeriesTable(perf.data.series.map((p) => p.date), [
                { name: 'Portfolio', values: perf.data.series.map((p) => p.cumulativeReturn) },
                { name: perf.data.benchmarkSymbol, values: perf.data.series.map((p) => p.benchmarkCumulativeReturn) },
              ], (v) => pct(v))}
            >
              <TimeSeriesChart
                dates={perf.data.series.map((p) => p.date)}
                series={[
                  { name: 'Portfolio', values: perf.data.series.map((p) => p.cumulativeReturn) },
                  { name: perf.data.benchmarkSymbol, values: perf.data.series.map((p) => p.benchmarkCumulativeReturn), dashed: true },
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
                    : 'Optimize the portfolio, review the trade proposal and confirm the simulated purchases to start tracking performance.'}
                  action={<Button variant="outline" render={<Link to={hasHoldings ? '/backtest' : '/optimize'} />}>{hasHoldings ? 'Run a backtest' : 'Go to optimizer'} <ArrowRight /></Button>}
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

      <div className="mt-4 grid gap-4 lg:grid-cols-3">
        <Card>
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle className="text-sm">Recent optimization runs</CardTitle>
            <Button variant="link" size="sm" render={<Link to="/optimize" />}>All</Button>
          </CardHeader>
          <CardContent className="space-y-2">
            {runs.data?.items.length === 0 && <p className="text-sm text-muted-foreground">No runs yet.</p>}
            {runs.data?.items.slice(0, 5).map((r) => (
              <Link key={r.id} to={`/optimize/${r.id}`} className="block rounded-md border px-3 py-2 text-sm hover:bg-muted/60">
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
            <Button variant="link" size="sm" render={<Link to="/trades" />}>Ledger</Button>
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
                  const n = status.data.coverage.filter((c) => c.isEnabled && c.freshness === f).length
                  return (
                    <div key={f} className="flex items-center justify-between">
                      <span className="text-muted-foreground">{f}</span>
                      <span className="tabular">{n} asset{n === 1 ? '' : 's'}</span>
                    </div>
                  )
                })}
                <div className="flex items-center justify-between">
                  <span className="text-muted-foreground">Last download</span>
                  <span>{relativeTime(status.data.coverage.map((c) => c.lastRetrievedAtUtc).filter(Boolean).sort().at(-1))}</span>
                </div>
              </>
            )}
          </CardContent>
        </Card>
      </div>

      <CurrencyConverterCard base={ccy} className="mt-6" />
    </>
  )
}
