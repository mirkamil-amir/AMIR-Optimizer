import { Fragment, useState } from 'react'
import { Link, useParams } from 'react-router'
import { ArrowLeft, ChevronDown, ChevronRight } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Legend, RiskContributionChart, seriesColor } from '@/components/charts'
import { DataBadge, ErrorState, InfoTip, LoadingBlock, MetricCard, MetricValue, PageHeader } from '@/components/common'
import { ChangeText } from '@/components/watchlist'
import { date, dateTime, num, objectiveLabels, pct } from '@/lib/format'
import { useRealityCheck } from '@/lib/queries'
import type { HistoricalCheck, RealityCheck, RealityFinding } from '@/lib/types'
import { cn } from '@/lib/utils'

const kindStyles: Record<RealityFinding['kind'], string> = {
  Fact: 'border-border text-foreground',
  Estimate: 'border-border text-muted-foreground',
  Interpretation: 'border-series-1/50 text-foreground',
  Caution: 'border-warn/60 text-warn',
}

const kindHelp: Record<RealityFinding['kind'], string> = {
  Fact: 'Computed directly from stored prices and the saved weights.',
  Estimate: 'Output of a statistical model (e.g. the covariance estimate); uncertain.',
  Interpretation: 'A documented rule applied to a computed number.',
  Caution: 'A limitation that weakens any conclusion.',
}

/** Portfolio Reality Check: what the run shows, what drives it, where it is fragile, and what limits the conclusion. */
export function RealityCheckPage() {
  const { runId } = useParams()
  const check = useRealityCheck(runId)
  const r = check.data

  return (
    <div className="space-y-6">
      <Button variant="ghost" size="sm" className="-ml-2" render={<Link to={runId ? `/optimize/${runId}` : '/optimize'} />}><ArrowLeft /> Optimization result</Button>
      <PageHeader
        title="Portfolio reality check"
        description="Challenges a saved optimization: concentration, risk, behaviour in bad periods, sensitivity to assumptions and costs. Every statement is computed from stored data and labelled by how it is supported."
      />
      {check.error ? <ErrorState error={check.error} onRetry={() => check.refetch()} /> : !r ? <LoadingBlock height="h-96" /> : <Report r={r} />}
    </div>
  )
}

function Report({ r }: { r: RealityCheck }) {
  const topics = [...new Set(r.findings.map((f) => f.topic))]
  return (
    <>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <Badge variant="secondary">{objectiveLabels[r.objective] ?? r.objective}</Badge>
        {r.usesSimulatedData ? <DataBadge kind="simulated" /> : <DataBadge kind="estimate" label="Historical data" />}
        {r.dataChangedSinceRun && <Badge variant="outline" className="border-warn/50 text-warn">Stored data changed since the run</Badge>}
        <span className="text-muted-foreground">
          Run of {dateTime(r.runCreatedAtUtc)} · window {date(r.windowStart)} – {date(r.windowEnd)} ({r.observations} daily returns) · {r.currency}
        </span>
      </div>

      <section aria-labelledby="findings">
        <h2 id="findings" className="mb-3 text-base font-semibold">What the analysis shows</h2>
        <div className="grid gap-3 md:grid-cols-2">
          {topics.map((topic) => (
            <Card key={topic} className="gap-2 py-4">
              <CardHeader className="px-4"><CardTitle className="text-sm">{topic}</CardTitle></CardHeader>
              <CardContent className="space-y-2 px-4">
                {r.findings.filter((f) => f.topic === topic).map((f) => (
                  <div key={f.text} className="flex gap-2 text-sm">
                    <Badge variant="outline" className={cn('h-fit shrink-0', kindStyles[f.kind])} title={kindHelp[f.kind]}>{f.kind}</Badge>
                    <p>
                      {f.text}
                      {f.basis && <span className="ml-1 inline-flex align-middle"><InfoTip label="Basis">Basis: {f.basis}.</InfoTip></span>}
                    </p>
                  </div>
                ))}
              </CardContent>
            </Card>
          ))}
        </div>
      </section>

      <section aria-labelledby="concentration" className="space-y-3">
        <h2 id="concentration" className="text-base font-semibold">Concentration</h2>
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
          <MetricCard label="Positions" value={r.concentration.holdings} sub="weight ≥ 0.5%" />
          <MetricCard label="Effective holdings" value={num(r.concentration.effectiveHoldings, 1)} sub={`HHI ${num(r.concentration.hhi, 3)}`}
            hint="1 / Σw² of the invested weights: the number of equally weighted positions with the same concentration." />
          <MetricCard label="Largest position" value={pct(r.concentration.largestWeight, 1)} sub={r.concentration.largestSymbol} />
          <MetricCard label="Top three" value={pct(r.concentration.topThreeWeight, 1)} />
        </div>
        <Card className="py-4">
          <CardContent className="space-y-1.5 px-4">
            <div className="text-xs font-medium text-muted-foreground">Sector weights (catalogue classification)</div>
            {r.concentration.sectors.map((s) => (
              <div key={s.sector} className="grid grid-cols-[10rem_1fr_4rem] items-center gap-2 text-sm">
                <span className="truncate">{s.sector}</span>
                <div className="h-2 rounded-full bg-muted" aria-hidden><div className="h-2 rounded-full bg-series-1" style={{ width: `${Math.min(100, s.weight * 100)}%` }} /></div>
                <span className="text-right tabular">{pct(s.weight, 1)}</span>
              </div>
            ))}
          </CardContent>
        </Card>
      </section>

      <section aria-labelledby="risk" className="space-y-3">
        <h2 id="risk" className="text-base font-semibold">Risk and correlation <span className="text-xs font-normal text-muted-foreground">(estimates)</span></h2>
        <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
          <MetricCard label="Volatility" value={pct(r.risk.volatility, 1)} sub="annualized, ex-ante" hint="√(wᵀΣw) with the run's covariance estimate." />
          <MetricCard label="Diversification ratio" value={num(r.risk.diversificationRatio, 2)} hint="Σwᵢσᵢ / σₚ. 1 = no reduction of risk from combining the stocks; higher = more diversification benefit." />
          <MetricCard label="Effective risk contributors" value={r.risk.effectiveRiskContributors === null ? 'n/a' : num(r.risk.effectiveRiskContributors, 1)}
            hint="1 / Σ(risk share)²: how many equally risky positions the risk is spread over." />
          <MetricCard label="Avg. correlation" value={r.risk.averageCorrelation === null ? 'n/a' : num(r.risk.averageCorrelation, 2)}
            sub={r.risk.averageCorrelationOnStressDays !== null ? `${num(r.risk.averageCorrelationOnStressDays, 2)} on the ${r.risk.stressDays} worst days` : undefined}
            hint="Average pairwise correlation of the holdings over the window, and on the worst 10% of days of an equal-weight basket of them." />
        </div>
        <Card>
          <CardHeader><CardTitle className="text-sm">Weight vs. share of risk</CardTitle>
            <CardDescription className="text-xs">Risk share = wᵢ(Σw)ᵢ / wᵀΣw. A bar much longer for risk than for weight means the position adds more risk than its size suggests.</CardDescription>
          </CardHeader>
          <CardContent>
            <Legend items={[{ name: 'Weight', color: seriesColor(0) }, { name: 'Share of risk', color: seriesColor(1) }]} />
            <RiskContributionChart items={r.risk.contributions.map((c) => ({ symbol: c.symbol, weight: c.weight, risk: c.riskShare }))} />
          </CardContent>
        </Card>
      </section>

      <section aria-labelledby="history" className="space-y-3">
        <h2 id="history" className="text-base font-semibold">Historical behaviour</h2>
        <div className="grid gap-3 md:grid-cols-2">
          <HistoryCard h={r.inSample} />
          {r.outOfSample ? <HistoryCard h={r.outOfSample} /> : (
            <Card className="py-4"><CardContent className="px-4 text-sm text-muted-foreground">
              <div className="mb-1 font-medium text-foreground">Out-of-sample</div>{r.outOfSampleUnavailableReason}
            </CardContent></Card>
          )}
        </div>
      </section>

      <Sensitivity r={r} />

      <section aria-labelledby="costs" className="space-y-2">
        <h2 id="costs" className="text-base font-semibold">Costs <span className="text-xs font-normal text-muted-foreground">(estimates)</span></h2>
        <Card className="py-4"><CardContent className="space-y-1 px-4 text-sm">
          <p>Building the portfolio from cash: about <strong className="tabular">{pct(r.costs.buildCost, 2)}</strong> of its value at {r.costs.costBps} bps.</p>
          {r.costs.switchCost !== null && (
            <p>Switching to the most different scenario ({pct(r.costs.largestSwitchTurnover, 0)} turnover): <strong className="tabular">{pct(r.costs.switchCost, 2)}</strong>
              {' '}({pct(r.costs.switchCostAtTripleBps, 2)} at three times the cost rate).</p>
          )}
          <p className="text-xs text-muted-foreground">{r.costs.note}</p>
        </CardContent></Card>
      </section>

      <section className="grid gap-3 md:grid-cols-2">
        <Card className="py-4">
          <CardHeader className="px-4"><CardTitle className="text-sm">What to investigate next</CardTitle>
            <CardDescription className="text-xs">Educational suggestions – not recommendations to buy or sell.</CardDescription></CardHeader>
          <CardContent className="px-4"><ul className="list-disc space-y-1 pl-4 text-sm">{r.nextSteps.map((s) => <li key={s}>{s}</li>)}</ul>
            <div className="mt-3 flex flex-wrap gap-2">
              <Button size="sm" variant="outline" render={<Link to="/backtest" />}>Backtest</Button>
              <Button size="sm" variant="outline" render={<Link to="/strategy-comparison" />}>Strategy comparison</Button>
              <Button size="sm" variant="outline" render={<Link to="/learn" />}>Strategy guide</Button>
            </div>
          </CardContent>
        </Card>
        <Card className="py-4">
          <CardHeader className="px-4"><CardTitle className="text-sm">What limits these conclusions</CardTitle></CardHeader>
          <CardContent className="px-4"><ul className="list-disc space-y-1 pl-4 text-sm text-muted-foreground">{r.limitations.map((l) => <li key={l}>{l}</li>)}</ul></CardContent>
        </Card>
      </section>

      <details className="text-xs text-muted-foreground">
        <summary className="cursor-pointer font-medium text-foreground">Methodology</summary>
        <p className="mt-2 max-w-3xl leading-relaxed">{r.methodology}</p>
      </details>
      <p className="rounded-md border p-3 text-xs">{r.disclaimer}</p>
    </>
  )
}

function HistoryCard({ h }: { h: HistoricalCheck }) {
  return (
    <Card className="py-4">
      <CardContent className="px-4">
        <div className="mb-2 text-sm font-medium">{h.label}</div>
        <div className="mb-2 text-xs text-muted-foreground">{date(h.from)} – {date(h.to)} · {h.days} days</div>
        <dl className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-sm">
          <Row label="Cumulative return"><ChangeText value={h.cumulativeReturn} /></Row>
          <Row label={`Benchmark ${h.benchmarkSymbol ?? ''}`}>{h.benchmarkCumulativeReturn === null ? <span className="text-muted-foreground">not available</span> : <ChangeText value={h.benchmarkCumulativeReturn} />}</Row>
          <Row label="Volatility (realized)"><MetricValue metric={h.annualizedVolatility} format={(v) => pct(v, 1)} /></Row>
          <Row label="Maximum drawdown"><MetricValue metric={h.maxDrawdown} format={(v) => pct(v, 1)} /></Row>
          <Row label="1-day VaR 95%"><MetricValue metric={h.valueAtRisk95} format={(v) => pct(v, 2)} /></Row>
          <Row label="Expected shortfall 95%"><MetricValue metric={h.expectedShortfall95} format={(v) => pct(v, 2)} /></Row>
          <Row label="Worst day">{pct(h.worstDay, 2)}</Row>
          <Row label="Worst 20 days">{h.worst20DayReturn === null ? '—' : pct(h.worst20DayReturn, 1)}</Row>
          {h.beta && <Row label="Beta to benchmark"><MetricValue metric={h.beta} format={(v) => num(v, 2)} /></Row>}
        </dl>
      </CardContent>
    </Card>
  )
}

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="tabular">{children}</dd>
    </div>
  )
}

function Sensitivity({ r }: { r: RealityCheck }) {
  const [open, setOpen] = useState<string | null>(null)
  return (
    <section aria-labelledby="sensitivity" className="space-y-2">
      <h2 id="sensitivity" className="flex items-center gap-1.5 text-base font-semibold">
        Sensitivity to assumptions
        <InfoTip>Each row solves the same objective and constraints again with one assumption changed. Turnover = ½Σ|Δw| versus the saved weights: the share of the portfolio that would change. Risk and return are measured with the run's own estimates.</InfoTip>
      </h2>
      <Card className="py-2">
        <CardContent className="overflow-x-auto px-2">
          <table className="w-full min-w-[36rem] text-sm">
            <caption className="sr-only">Sensitivity scenarios</caption>
            <thead className="text-xs text-muted-foreground">
              <tr className="border-b text-left">
                <th className="p-2 font-medium">Scenario</th>
                <th className="p-2 text-right font-medium">Turnover</th>
                <th className="p-2 text-right font-medium">Largest change</th>
                <th className="p-2 text-right font-medium">Volatility</th>
                <th className="p-2 text-right font-medium">Exp. return</th>
              </tr>
            </thead>
            <tbody>
              {r.scenarios.map((s) => (
                <Fragment key={s.key}>
                  <tr className="border-b align-top last:border-0">
                    <td className="p-2">
                      <button type="button" className="flex items-start gap-1 text-left font-medium hover:underline disabled:no-underline" disabled={s.weights.length === 0}
                        aria-expanded={open === s.key} onClick={() => setOpen(open === s.key ? null : s.key)}>
                        {s.weights.length > 0 && (open === s.key ? <ChevronDown className="mt-0.5 size-4" /> : <ChevronRight className="mt-0.5 size-4" />)}
                        {s.name}
                      </button>
                      <div className="text-xs text-muted-foreground">{s.status === 'Optimal' ? s.description : `${s.status}: ${s.message}`}</div>
                    </td>
                    <td className="p-2 text-right tabular">{s.turnover === null ? '—' : pct(s.turnover, 1)}</td>
                    <td className="p-2 text-right tabular">{s.largestChange === null ? '—' : `${s.largestChangeSymbol} ${pct(s.largestChange, 1)}`}</td>
                    <td className="p-2 text-right tabular">{s.volatility === null ? '—' : pct(s.volatility, 1)}</td>
                    <td className="p-2 text-right tabular">{s.expectedReturn === null ? '—' : pct(s.expectedReturn, 1)}</td>
                  </tr>
                  {open === s.key && (
                    <tr className="border-b bg-muted/40">
                      <td colSpan={5} className="p-2">
                        <table className="w-full text-xs tabular">
                          <thead><tr className="text-left text-muted-foreground"><th className="py-1">Stock</th><th className="py-1 text-right">Saved</th><th className="py-1 text-right">Scenario</th><th className="py-1 text-right">Change</th></tr></thead>
                          <tbody>
                            {s.weights.filter((w) => w.weight > 0.0005 || w.savedWeight > 0.0005).map((w) => (
                              <tr key={w.symbol}><td className="py-0.5">{w.symbol}</td><td className="py-0.5 text-right">{pct(w.savedWeight, 1)}</td>
                                <td className="py-0.5 text-right">{pct(w.weight, 1)}</td><td className="py-0.5 text-right">{pct(w.weight - w.savedWeight, 1, true)}</td></tr>
                            ))}
                          </tbody>
                        </table>
                      </td>
                    </tr>
                  )}
                </Fragment>
              ))}
            </tbody>
          </table>
        </CardContent>
      </Card>
    </section>
  )
}
