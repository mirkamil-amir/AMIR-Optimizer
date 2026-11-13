import { useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQueryClient } from '@tanstack/react-query'
import { Link, NavLink, useParams } from 'react-router'
import { toast } from 'sonner'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from '@/components/ui/table'
import { EmptyState, ErrorState, InfoTip, LoadingBlock, MetricCard, MetricGroup, MetricValue, PageHeader, ResultSection, Warnings } from '@/components/common'
import { SearchableTable, type Column } from '@/components/data-table'
import { TimeSeriesChart, Legend, seriesColor } from '@/components/charts'
import { MatrixLoader, WorkingPanel } from '@/components/matrix-loader'
import { errorMessage } from '@/lib/api'
import { dateTime, money, pct, relativeTime } from '@/lib/format'
import {
  useCreatePilot, useDisablePilot, useEnablePilot, useKillPilot, usePilot, usePilotComparison, usePilotCycle, usePilotCycles, usePilotEvents,
  usePilotHome, usePilotMandates, usePilotScorecard, useResumePilot, useRunPilot, useUpdatePilotMandate,
} from '@/lib/queries'
import type {
  Pilot, PilotComparison, PilotCycleDetail, PilotCycleSummary, PilotEvent, PilotFrequency, PilotMandate, PilotModel, PilotOutcome, PilotOverview,
  PilotPortfolioRow, PilotStep,
} from '@/lib/types'
import { cn } from '@/lib/utils'

// AI Probe › Matrix Pilot (docs/matrix-pilot.md): the control room of an AI-managed experimental (paper) portfolio. The page
// shows what the model decided and what the platform calculated, validated and booked – always labelled apart.

export const PILOT_LABEL = 'Matrix Pilot – AI-managed experimental portfolio'

// ================================================================ AI Probe sub-navigation

/** The two parts of AI Probe (text links; the selected one is marked for screen readers too). */
export function ProbeTabs() {
  const cls = ({ isActive }: { isActive: boolean }) =>
    cn('rounded-md px-3 py-1.5 text-sm font-medium', isActive ? 'bg-muted text-foreground' : 'text-muted-foreground hover:text-foreground')
  return (
    <nav aria-label="AI Probe" className="mb-5 flex gap-1 border-b pb-2">
      <NavLink to="/research" end className={cls}>Research assistant</NavLink>
      <NavLink to="/research/pilot" className={cls}>Matrix Pilot</NavLink>
    </nav>
  )
}

// ================================================================ outcome / status labels

const outcomeStyle: Record<PilotOutcome, string> = {
  Pending: 'border-series-1/50 text-foreground',
  Rebalanced: 'border-gain/50 text-gain',
  NoTrade: 'border-border text-foreground',
  Skipped: 'border-warn/50 text-warn',
  Blocked: 'border-loss/50 text-loss',
  Failed: 'border-loss/50 text-loss',
  PausedByDrawdown: 'border-warn/50 text-warn',
}

export function OutcomeBadge({ outcome, label }: { outcome: PilotOutcome; label: string }) {
  return <Badge variant="outline" className={outcomeStyle[outcome]}>{label}</Badge>
}

function StatusBadge({ status }: { status: string }) {
  const style = status === 'ACTIVE' || status === 'RUNNING' ? 'border-gain/50 text-gain' : status === 'DISABLED' ? 'border-border text-muted-foreground' : 'border-warn/50 text-warn'
  return <Badge variant="outline" className={cn('font-semibold', style)}>{status}</Badge>
}

const phaseLabel: Record<string, string> = {
  Queued: 'queued', Observe: 'observing the portfolio and data', Research: 'researching with tools', Construct: 'deciding', Critique: 'risk review',
  Finalize: 'finalising the decision', Validate: 'validating', Execute: 'booking simulated trades', Journal: 'writing the journal', Complete: 'complete',
}

function modelText(m: { provider: string; model: string }, isTest?: boolean) {
  return isTest || m.provider === 'Mock' ? `${m.provider} ${m.model} (test script – not an AI)` : `${m.provider} ${m.model}`
}

// ================================================================ AI Probe › Matrix Pilot (list of portfolios)

export function PilotHomePage() {
  const home = usePilotHome()
  const columns: Column<PilotPortfolioRow>[] = [
    { key: 'name', header: 'Portfolio', cell: (r) => <Link className="font-medium hover:underline" to={`/research/pilot/${r.portfolioId}`}>{r.name}</Link>, sortValue: (r) => r.name },
    { key: 'ccy', header: 'Currency', cell: (r) => r.baseCurrency, sortValue: (r) => r.baseCurrency },
    {
      key: 'pilot', header: 'Matrix Pilot', sortValue: (r) => r.pilotStatus ?? '',
      cell: (r) => r.pilotStatus ? <StatusBadge status={r.running ? 'RUNNING' : r.pilotStatus === 'KillSwitched' ? 'KILL SWITCH' : r.pilotStatus.toUpperCase()} /> : <span className="text-muted-foreground">not set up</span>,
    },
    { key: 'last', header: 'Last decision', cell: (r) => r.lastDecisionAtUtc ? <span title={dateTime(r.lastDecisionAtUtc)}>{relativeTime(r.lastDecisionAtUtc)}</span> : '—', sortValue: (r) => r.lastDecisionAtUtc ?? '' },
    { key: 'next', header: 'Next decision', cell: (r) => r.nextDecisionAtUtc ? dateTime(r.nextDecisionAtUtc) : '—', sortValue: (r) => r.nextDecisionAtUtc ?? '' },
    { key: 'status', header: 'Portfolio', cell: (r) => r.status, sortValue: (r) => r.status },
  ]
  return (
    <>
      <ProbeTabs />
      <PageHeader
        title="Matrix Pilot"
        info="An AI model manages one paper portfolio on its own: it researches with the platform's tools, chooses holdings and weights and decides when to rebalance. The platform checks every decision against your mandate before anything is booked, records every step and measures the result from the day you switch it on."
        description={PILOT_LABEL + '. Paper money only; results are measured, not promised.'}
      />
      {home.error ? <ErrorState error={home.error} onRetry={() => home.refetch()} /> : !home.data ? <LoadingBlock height="h-40" /> : (
        <div className="space-y-4">
          <ModelNote model={home.data.configuredModel} />
          <SearchableTable label="portfolios" rows={home.data.portfolios} columns={columns} rowKey={(r) => r.portfolioId} placeholder="Search portfolios"
            matches={(r, q) => r.name.toLowerCase().includes(q)} empty="You have no portfolio yet. Create one with Forge, then set up Matrix Pilot for it."
            initialSort={{ key: 'name', dir: 'asc' }} />
        </div>
      )}
    </>
  )
}

function ModelNote({ model }: { model: PilotModel }) {
  if (!model.available)
    return (
      <Alert>
        <AlertTitle>No language model is configured</AlertTitle>
        <AlertDescription>Matrix Pilot needs one (Agent:Llm:Provider on the server). It never falls back to fixed rules, so it cannot be switched on until a model is configured.</AlertDescription>
      </Alert>
    )
  return (
    <p className="text-sm text-muted-foreground">
      Configured model: <span className="font-medium text-foreground">{modelText(model, model.isTestModel)}</span>.
      {model.isTestModel && ' This environment uses a deterministic test script; its decisions are not AI decisions.'}
    </p>
  )
}

// ================================================================ control room

export function PilotControlRoomPage() {
  const { portfolioId } = useParams()
  const overview = usePilot(portfolioId)
  const [selected, setSelected] = useState<string | undefined>()
  if (overview.error) return <><ProbeTabs /><ErrorState error={overview.error} onRetry={() => overview.refetch()} /></>
  if (!overview.data) return <><ProbeTabs /><LoadingBlock /></>
  const o = overview.data
  return (
    <>
      <ProbeTabs />
      <PageHeader
        title={`Matrix Pilot · ${o.portfolioName}`}
        info="The control room of this portfolio's AI manager: status, the latest decision, every tool it used, the validation, the bookings and the live record."
        description={o.label}
        actions={o.pilot ? <PilotActions overview={o} pilot={o.pilot} /> : undefined}
      />
      {!o.pilot ? <SetupCard overview={o} /> : <ControlRoom overview={o} pilot={o.pilot} selected={selected} onSelect={setSelected} />}
    </>
  )
}

function ControlRoom({ overview, pilot, selected, onSelect }: { overview: PilotOverview; pilot: Pilot; selected?: string; onSelect: (id: string) => void }) {
  const running = pilot.displayStatus === 'RUNNING'
  // When a cycle finishes or the status changes, everything derived from it (journal, timeline, record, scorecard, audit) is reloaded.
  const qc = useQueryClient()
  const stamp = `${pilot.status}|${pilot.latestCycle?.id ?? ''}|${pilot.latestCycle?.outcome ?? ''}|${pilot.mandateVersion}`
  useEffect(() => {
    qc.invalidateQueries({ queryKey: ['pilot', pilot.portfolioId], predicate: (q) => q.queryKey.length > 2 })
  }, [qc, pilot.portfolioId, stamp])
  const cycles = usePilotCycles(pilot.portfolioId, true, running)
  const cycleId = selected ?? pilot.latestDecision?.id ?? pilot.latestCycle?.id
  const detail = usePilotCycle(pilot.portfolioId, cycleId, running && cycleId === pilot.latestCycle?.id)
  return (
    <div className="space-y-8">
      {pilot.status === 'KillSwitched' || pilot.status === 'Paused' ? (
        <Alert>
          <AlertTitle>{pilot.status === 'KillSwitched' ? 'Stopped by the kill switch' : 'Paused by the system'} {pilot.pausedAtUtc && `· ${dateTime(pilot.pausedAtUtc)}`}</AlertTitle>
          <AlertDescription>{pilot.pauseReason} No decision or trade happens until you resume. Booked trades stay in the ledger.</AlertDescription>
        </Alert>
      ) : null}
      {!pilot.modelMatchesConfiguration && (
        <Alert>
          <AlertTitle>The configured model changed</AlertTitle>
          <AlertDescription>
            This pilot was set up with {pilot.provider} {pilot.model}; the server now runs {overview.configuredModel.provider} {overview.configuredModel.model}. Cycles fail (no trade)
            until you confirm the change in the mandate, so decisions of different models are never mixed up.
          </AlertDescription>
        </Alert>
      )}
      {running && <WorkingPanel title={`Deciding: ${phaseLabel[pilot.runningPhase ?? "Queued"]}`} detail="The journal below fills as the cycle advances. The kill switch stops it before anything is booked." />}

      <StatusSection pilot={pilot} />
      <DecisionSection pilot={pilot} detail={detail.data} loading={detail.isLoading} />
      <ActivitySection detail={detail.data} />
      <ResultSection title="Decision timeline" hint="Every cycle with what it ended in. NO TRADE is a decision; SKIPPED, BLOCKED and FAILED are not, and say why. Select a row to inspect it above.">
        {cycles.error ? <ErrorState error={cycles.error} /> : !cycles.data ? <LoadingBlock height="h-32" /> : (
          <SearchableTable label="cycles" rows={cycles.data.items} rowKey={(c) => c.id} onRowClick={(c) => onSelect(c.id)} placeholder="Search cycles"
            matches={(c, q) => `${c.outcomeLabel} ${c.outcomeReason ?? ''} ${c.approach ?? ''}`.toLowerCase().includes(q)} initialSort={{ key: 'at', dir: 'desc' }}
            empty="No decision yet. The first one runs at the next decision time (or when you choose Run decision now)." columns={cycleColumns(cycleId)} />
        )}
      </ResultSection>
      <LiveRecordSection pilot={pilot} />
      <ScorecardSection pilot={pilot} />
      <MandateSection overview={overview} pilot={pilot} />
      <AuditSection pilot={pilot} />
      <ResultSection title="What your result means" hint="How to read this experiment honestly.">
        <Card><CardContent className="space-y-2 pt-4 text-sm leading-relaxed">
          <p>Matrix Pilot is an experiment: can an AI model manage a portfolio end to end when every number comes from the platform's own calculations? Its record starts on {pilot.baseline ? dateTime(pilot.baseline.atUtc) : 'the day you switch it on'} and is never back-filled.</p>
          <p>A short record says little. Compare it with a simple alternative over the same dates (another portfolio, equal weight, the benchmark) and look at drawdown, turnover and costs as well as return. Blocked and failed decisions are part of the result.</p>
          <p>Paper money only: no real order is sent anywhere. This is educational research, not investment advice, and past results do not predict future ones.</p>
        </CardContent></Card>
      </ResultSection>
    </div>
  )
}

function cycleColumns(selected?: string): Column<PilotCycleSummary>[] {
  return [
    { key: 'at', header: 'Decision time', cell: (c) => <span className={cn(c.id === selected && 'font-semibold')}>{dateTime(c.asOfUtc)}</span>, sortValue: (c) => c.asOfUtc },
    { key: 'outcome', header: 'Outcome', cell: (c) => <OutcomeBadge outcome={c.outcome} label={c.outcomeLabel} />, sortValue: (c) => c.outcome },
    { key: 'approach', header: 'Approach', cell: (c) => c.approach ?? '—', sortValue: (c) => c.approach ?? '' },
    { key: 'trades', header: 'Trades', align: 'right', cell: (c) => c.tradesBooked, sortValue: (c) => c.tradesBooked },
    { key: 'tools', header: 'Tool calls', align: 'right', cell: (c) => c.toolCalls, sortValue: (c) => c.toolCalls },
    { key: 'why', header: 'Reason', cell: (c) => <span className="line-clamp-2 max-w-md text-muted-foreground">{c.outcomeReason ?? phaseLabel[c.phase]}</span> },
  ]
}

// ---------------------------------------------------------------- status

function StatusSection({ pilot }: { pilot: Pilot }) {
  const cmp = usePilotComparison(pilot.portfolioId, undefined, !!pilot.baseline)
  const live = cmp.data?.pilot
  const e = pilot.evaluation
  const ccy = pilot.baseCurrency
  return (
    <ResultSection title="Status" hint="Where the pilot stands now. Figures come from the ledger and stored prices; the live record starts at the baseline.">
      <MetricGroup title="Matrix Pilot" columns={4}>
        <MetricCard label="Status" value={<StatusBadge status={pilot.displayStatus} />} sub={pilot.status === 'Active' ? `since ${dateTime(pilot.enabledAtUtc)}` : pilot.pauseReason ?? undefined} />
        <MetricCard label="AI model" value={<span className="text-base">{modelText(pilot)}</span>} sub={`Effort ${pilot.effort}`} hint="Recorded on every decision, so a later model change never relabels old decisions." />
        <MetricCard label="Decision frequency" value={pilot.frequency} sub={`Mandate version ${pilot.mandateVersion}`} />
        <MetricCard label="Next decision" value={<span className="text-base">{pilot.nextDecisionAtUtc ? dateTime(pilot.nextDecisionAtUtc) : '—'}</span>}
          sub={pilot.lastDecisionAtUtc ? `Last: ${relativeTime(pilot.lastDecisionAtUtc)}` : 'No decision yet'} />
      </MetricGroup>
      <MetricGroup title="Live record" period={pilot.baseline ? `since ${dateTime(pilot.baseline.atUtc)}` : undefined}
        hint="Time-weighted return of this portfolio's ledger from the baseline; deposits and withdrawals are flows, not returns.">
        <MetricCard label="Return since baseline" value={<MetricValue metric={live?.totalReturn} format={(v) => pct(v, 2, true)} />} tone="signed" numeric={live?.totalReturn.value} />
        <MetricCard label="Maximum drawdown" value={<MetricValue metric={live?.maxDrawdown} format={(v) => pct(v)} />} sub={`Pause at ${pct(pilot.mandate.drawdownPauseThreshold, 0)}`} />
        <MetricCard label="Cash" value={live?.cashWeight == null ? '—' : pct(live.cashWeight, 1)} sub={live ? `${live.holdings} holdings` : undefined} />
        <MetricCard label="Benchmark" value={<span className="text-base">{cmp.data?.benchmark?.label ?? pilot.mandate.benchmark ?? '—'}</span>}
          sub={cmp.data?.benchmark ? <MetricValue metric={cmp.data.benchmark.totalReturn} format={(v) => pct(v, 2, true)} /> : undefined} />
      </MetricGroup>
      <MetricGroup title="Behaviour" hint="Counted from the recorded cycles; nothing is selected or smoothed.">
        <MetricCard label="Decisions" value={e.rebalanced + e.noTrade} sub={`${e.rebalanced} rebalance(s), ${e.noTrade} no trade`} />
        <MetricCard label="Not decided" value={e.skipped + e.blocked + e.failed} sub={`${e.blocked} blocked, ${e.failed} failed, ${e.skipped} skipped`} />
        <MetricCard label="Turnover" value={pct(e.turnoverSum, 1)} sub={e.averageTurnoverPerRebalance == null ? undefined : `${pct(e.averageTurnoverPerRebalance, 1)} per rebalance`}
          hint="One-way turnover ½Σ|w − w₀| of the executed rebalances, added up." />
        <MetricCard label="Costs" value={money(e.fees, ccy)} sub={e.costDragPct == null ? undefined : `${pct(e.costDragPct, 3)} of the starting value`} />
      </MetricGroup>
    </ResultSection>
  )
}

// ---------------------------------------------------------------- current decision

function DecisionSection({ pilot, detail, loading }: { pilot: Pilot; detail?: PilotCycleDetail; loading: boolean }) {
  if (loading) return <LoadingBlock height="h-48" />
  if (!detail) return (
    <ResultSection title="AI decision"><EmptyState title="No decision yet" description="The first decision runs at the next decision time." /></ResultSection>
  )
  const d = detail.decision
  const s = detail.summary
  const held = new Map((detail.observation?.holdings ?? []).map((h) => [h.symbol, h.weight ?? 0]))
  const targets = new Map((d?.targets ?? []).map((t) => [t.symbol, t.weight]))
  const changes = d && d.action === 'REBALANCE' ? {
    selected: [...targets.keys()].filter((k) => !held.has(k)),
    removed: [...held.keys()].filter((k) => !targets.has(k)),
    increased: [...targets.entries()].filter(([k, w]) => held.has(k) && w > (held.get(k) ?? 0) + 0.0005).map(([k]) => k),
    reduced: [...targets.entries()].filter(([k, w]) => held.has(k) && w < (held.get(k) ?? 0) - 0.0005).map(([k]) => k),
  } : null
  const ccy = pilot.baseCurrency
  return (
    <ResultSection title="AI decision" hint="What the model decided (AI DECISION), next to what the platform checked and booked. A blocked proposal is shown as proposed – never quietly changed to fit.">
      <Card>
        <CardHeader className="flex flex-row flex-wrap items-center gap-2">
          <CardTitle className="text-base">{d?.action ?? 'No decision'} · {dateTime(s.asOfUtc)}</CardTitle>
          <OutcomeBadge outcome={s.outcome} label={s.outcomeLabel} />
          {d && <Badge variant="outline">{d.strategy.approach}</Badge>}
          {d && <Badge variant="outline">confidence {d.confidence}</Badge>}
          <span className="ml-auto text-xs text-muted-foreground">{modelText(s)} · mandate v{s.mandateVersion}</span>
        </CardHeader>
        <CardContent className="space-y-4 text-sm">
          {s.outcomeReason && <p className={cn(s.outcome === 'Blocked' || s.outcome === 'Failed' ? 'text-loss' : 'text-muted-foreground')}>{s.outcomeReason}</p>}
          {detail.narrative.length > 0 && (
            <div className="space-y-1">
              {detail.narrative.map((n) => n.paragraphs.length > 0 && (
                <p key={n.key}><span className="font-medium">{n.title}: </span>{n.paragraphs.join(' ')}</p>
              ))}
              <p className="text-xs text-muted-foreground">
                {detail.narrativeSource === 'model' ? 'Report written by the model and checked against the recorded evidence (every number and symbol).' : 'Report assembled by the platform from the recorded decision.'}
              </p>
            </div>
          )}
          {d && (
            <div className="grid gap-4 md:grid-cols-2">
              <Facts title="Why" items={d.rationale} />
              <Facts title="Strategy" items={[d.strategy.reason, d.strategy.components.length ? `Components: ${d.strategy.components.join(', ')}` : ''].filter(Boolean)} />
              <Facts title="Key risks" items={d.keyRisks} />
              <Facts title="What would invalidate it" items={d.invalidationConditions} />
              {changes && <Facts title="Changes" items={[
                changes.selected.length ? `Selected: ${changes.selected.join(', ')}` : '', changes.removed.length ? `Removed: ${changes.removed.join(', ')}` : '',
                changes.increased.length ? `Increased: ${changes.increased.join(', ')}` : '', changes.reduced.length ? `Reduced: ${changes.reduced.join(', ')}` : '',
              ].filter(Boolean)} />}
              <Facts title="Horizon and requests" items={[`Expected holding period: ${d.expectedHoldingDays} days`, ...d.mandateRequests.map((r) => `Asked the owner: ${r}`)]} />
            </div>
          )}
          {detail.critique && (
            <div className="rounded-lg border p-3">
              <p className="font-medium">Risk review (second model call): {detail.critique.recommendation}</p>
              <p className="text-muted-foreground">{detail.critique.summary}</p>
              <ul className="mt-1 list-disc pl-5 text-muted-foreground">{detail.critique.concerns.map((c, i) => <li key={i}>{c.area} ({c.severity}): {c.detail}</li>)}</ul>
              {d?.critiqueResponse && <p className="mt-1"><span className="font-medium">Response: </span>{d.critiqueResponse}</p>}
            </div>
          )}
          {d && d.targets.length > 0 && <AllocationTable detail={detail} />}
          {detail.validation && (
            <div className="space-y-1">
              <p className="font-medium">Validation by the platform: {detail.validation.passed ? 'passed' : 'blocked'}</p>
              <ul className="space-y-0.5 text-muted-foreground">
                {detail.validation.checks.map((c) => <li key={c.name}><span className={c.passed ? 'text-gain' : 'text-loss'}>{c.passed ? 'passed' : 'failed'}</span> · {c.name}: {c.detail}</li>)}
              </ul>
              {detail.validation.violations.length > 0 && (
                <Table>
                  <TableHeader><TableRow><TableHead>Rule</TableHead><TableHead>AI proposed</TableHead><TableHead>Allowed</TableHead><TableHead>Detail</TableHead></TableRow></TableHeader>
                  <TableBody>
                    {detail.validation.violations.map((v, i) => (
                      <TableRow key={i}><TableCell>{v.rule}</TableCell><TableCell className="tabular">{v.proposed == null ? '—' : pct(v.proposed)}</TableCell>
                        <TableCell className="tabular">{v.allowed == null ? '—' : pct(v.allowed)}</TableCell><TableCell>{v.detail}</TableCell></TableRow>
                    ))}
                  </TableBody>
                </Table>
              )}
              {detail.validation.risk?.volatility != null && (
                <p className="text-xs text-muted-foreground">Ex-ante volatility {pct(detail.validation.risk.volatility, 1)}, effective holdings {detail.validation.risk.effectiveHoldings?.toFixed(1)}, window {detail.validation.risk.window} (historical estimate).</p>
              )}
              {detail.validation.trades && (
                <p className="text-xs text-muted-foreground">Estimated: {detail.validation.trades.buys} buy(s), {detail.validation.trades.sells} sell(s), turnover {pct(detail.validation.trades.turnover, 1)}, costs {money(detail.validation.trades.costs, ccy)}.</p>
              )}
            </div>
          )}
          {d && d.rejected.length > 0 && <Facts title="Considered and rejected" items={d.rejected.map((r) => `${r.symbol}: ${r.reason}`)} />}
          {detail.executed && <Warnings title="Proposed vs held after booking" items={detail.executed.notes} collapsible />}
        </CardContent>
      </Card>
    </ResultSection>
  )
}

function Facts({ title, items }: { title: string; items: string[] }) {
  if (!items.length) return null
  return (
    <div>
      <p className="font-medium">{title}</p>
      <ul className="list-disc pl-5 text-muted-foreground">{items.map((x, i) => <li key={i}>{x}</li>)}</ul>
    </div>
  )
}

function AllocationTable({ detail }: { detail: PilotCycleDetail }) {
  const d = detail.decision!
  const executed = new Map((detail.executed?.weights ?? []).map((w) => [w.symbol, w.executedWeight]))
  const held = new Map((detail.observation?.holdings ?? []).map((h) => [h.symbol, h.weight ?? 0]))
  const rows = [...d.targets.map((t) => ({ ...t, before: held.get(t.symbol) ?? 0, after: executed.get(t.symbol) })),
    ...[...held.keys()].filter((k) => !d.targets.some((t) => t.symbol === k)).map((k) => ({ symbol: k, weight: 0, role: 'sold', thesis: '—', whyNow: '', risks: '', invalidation: '', before: held.get(k) ?? 0, after: executed.get(k) }))]
  const columns: Column<(typeof rows)[number]>[] = [
    { key: 'symbol', header: 'Symbol', cell: (r) => <span className="font-medium">{r.symbol}</span>, sortValue: (r) => r.symbol },
    { key: 'before', header: 'Before', align: 'right', cell: (r) => pct(r.before, 1), sortValue: (r) => r.before },
    { key: 'ai', header: 'AI proposed', align: 'right', cell: (r) => pct(r.weight, 2), sortValue: (r) => r.weight },
    { key: 'after', header: 'Held after booking', align: 'right', cell: (r) => (r.after == null ? '—' : pct(r.after, 2)), sortValue: (r) => r.after ?? -1 },
    { key: 'role', header: 'Role', cell: (r) => r.role },
    { key: 'thesis', header: 'Thesis', cell: (r) => <span className="line-clamp-2 max-w-md">{r.thesis}</span> },
  ]
  return (
    <SearchableTable label="positions" caption="AI proposed portfolio and executed portfolio" rows={rows} rowKey={(r) => r.symbol} columns={columns}
      placeholder="Search positions" matches={(r, q) => r.symbol.toLowerCase().includes(q)} initialSort={{ key: 'ai', dir: 'desc' }} maxRows={10} dense
      breakdown={`${d.targets.length} target holdings, cash ${pct(d.cashWeight, 1)} proposed${detail.executed ? `, ${pct(detail.executed.cashWeight, 1)} after booking` : ''}`} />
  )
}

// ---------------------------------------------------------------- AI activity (tool trace)

function ActivitySection({ detail }: { detail?: PilotCycleDetail }) {
  if (!detail) return null
  const columns: Column<PilotStep>[] = [
    { key: 'seq', header: '#', align: 'right', cell: (s) => s.sequence, sortValue: (s) => s.sequence },
    { key: 'category', header: 'Category', cell: (s) => <span className="text-xs">{s.category}</span>, sortValue: (s) => s.category },
    { key: 'name', header: 'Step', cell: (s) => <span className="font-medium">{s.name}</span>, sortValue: (s) => s.name },
    { key: 'status', header: 'Result', cell: (s) => <span className={cn(s.status === 'ok' ? 'text-gain' : s.status === 'denied' || s.status === 'rejected' || s.status === 'error' ? 'text-loss' : 'text-warn')}>{s.status}</span>, sortValue: (s) => s.status },
    { key: 'ms', header: 'Time', align: 'right', cell: (s) => `${Math.round(s.durationMs)} ms`, sortValue: (s) => s.durationMs },
    { key: 'summary', header: 'Summary', cell: (s) => <StepSummary step={s} /> },
  ]
  return (
    <ResultSection title="AI activity" hint="Every step of the cycle in order: model calls (AI DECISION), tools the model requested and the platform ran, the platform's own calculations, validation, execution and the outcome. Structured records only – no hidden reasoning.">
      <p className="text-xs text-muted-foreground">
        {detail.summary.modelCalls} model call(s), {detail.summary.toolCalls} tool call(s), {detail.inputTokens.toLocaleString()} input and {detail.outputTokens.toLocaleString()} output tokens ·
        data {Math.round(detail.dataMs)} ms, tools {Math.round(detail.toolMs)} ms, model {Math.round(detail.modelMs)} ms, validation {Math.round(detail.validationMs)} ms,
        execution {Math.round(detail.executionMs)} ms, total {Math.round(detail.summary.totalMs)} ms · prompt {detail.promptVersion}, tools {detail.toolsVersion}, schema {detail.schemaVersion} · correlation {detail.correlationId.slice(0, 8)}
      </p>
      <SearchableTable label="steps" caption="Decision trace" rows={detail.steps} rowKey={(s) => String(s.sequence)} columns={columns} placeholder="Search steps"
        matches={(s, q) => `${s.name} ${s.category} ${s.summary}`.toLowerCase().includes(q)} initialSort={{ key: 'seq', dir: 'asc' }} maxRows={12} fullScreen="Decision trace" />
    </ResultSection>
  )
}

function StepSummary({ step }: { step: PilotStep }) {
  const [open, setOpen] = useState(false)
  const has = step.inputJson || step.outputJson
  return (
    <div className="max-w-xl text-sm">
      <span>{step.summary}</span>
      {has && <Button type="button" size="xs" variant="ghost" className="ml-1" aria-expanded={open} onClick={(e) => { e.stopPropagation(); setOpen((o) => !o) }}>{open ? 'Hide data' : 'Show data'}</Button>}
      {open && (
        <div className="mt-1 space-y-1">
          {step.inputJson && <pre className="max-h-48 overflow-auto rounded bg-muted p-2 text-xs whitespace-pre-wrap">{pretty(step.inputJson)}</pre>}
          {step.outputJson && <pre className="max-h-72 overflow-auto rounded bg-muted p-2 text-xs whitespace-pre-wrap">{pretty(step.outputJson)}</pre>}
          {step.outputHash && <p className="text-xs text-muted-foreground">Result hash {step.outputHash}</p>}
        </div>
      )}
    </div>
  )
}

function pretty(json: string) {
  try { return JSON.stringify(JSON.parse(json), null, 2) } catch { return json }
}

// ---------------------------------------------------------------- live record

/** Matrix Pilot's forward-only record against another of the user's portfolios and the benchmark (also shown in the Arena tab). */
export function LiveRecordSection({ pilot, title = 'Live record vs another portfolio' }: { pilot: Pilot; title?: string }) {
  const home = usePilotHome()
  const [other, setOther] = useState<string>('none')
  const cmp = usePilotComparison(pilot.portfolioId, other === 'none' ? undefined : other, true)
  const options = [{ value: 'none', label: 'Benchmark only' },
    ...(home.data?.portfolios.filter((p) => p.portfolioId !== pilot.portfolioId).map((p) => ({ value: p.portfolioId, label: `${p.name} (${p.baseCurrency})` })) ?? [])]
  return (
    <ResultSection title={title} hint="Forward-only: both series start at Matrix Pilot's baseline and are time-weighted returns in the same currency. No history before the baseline, nothing back-filled.">
      <div className="flex flex-wrap items-center gap-2">
        <Label htmlFor="pilot-compare" className="text-sm">Compare with</Label>
        <Select value={other} onValueChange={(v) => v && setOther(String(v))} items={options}>
          <SelectTrigger id="pilot-compare" className="w-72" aria-label="Compare with"><SelectValue /></SelectTrigger>
          <SelectContent>{options.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
        </Select>
      </div>
      {cmp.error ? <ErrorState error={cmp.error} /> : !cmp.data ? <LoadingBlock height="h-48" /> : <ComparisonBody data={cmp.data} />}
    </ResultSection>
  )
}

function ComparisonBody({ data }: { data: PilotComparison }) {
  const series = [data.pilot, data.other, data.benchmark].filter((s): s is NonNullable<typeof s> => !!s)
  const dates = useMemo(() => [...new Set(series.flatMap((s) => s.points.map((p) => p.date)))].sort(), [series])
  const chartSeries = series.map((s, i) => {
    const map = new Map(s.points.map((p) => [p.date, p.cumulativeReturn]))
    return { name: s.label, values: dates.map((d) => map.get(d) ?? null), color: seriesColor(i), dashed: s.key === 'benchmark' }
  })
  const rows: { label: string; render: (s: (typeof series)[number]) => ReactNode }[] = [
    { label: 'Total return', render: (s) => <MetricValue metric={s.totalReturn} format={(v) => pct(v, 2, true)} /> },
    { label: 'Annualized return', render: (s) => <MetricValue metric={s.annualizedReturn} format={(v) => pct(v, 2, true)} /> },
    { label: 'Volatility', render: (s) => <MetricValue metric={s.volatility} format={(v) => pct(v)} /> },
    { label: 'Sharpe ratio', render: (s) => <MetricValue metric={s.sharpe} format={(v) => v.toFixed(2)} /> },
    { label: 'Sortino ratio', render: (s) => <MetricValue metric={s.sortino} format={(v) => v.toFixed(2)} /> },
    { label: 'Maximum drawdown', render: (s) => <MetricValue metric={s.maxDrawdown} format={(v) => pct(v)} /> },
    { label: 'Trades', render: (s) => (s.key === 'benchmark' ? '—' : s.trades) },
    { label: 'Fees', render: (s) => (s.key === 'benchmark' ? '—' : money(s.fees, data.currency)) },
    { label: 'Cash now', render: (s) => (s.cashWeight == null ? '—' : pct(s.cashWeight, 1)) },
    { label: 'Holdings / largest', render: (s) => (s.key === 'benchmark' ? '—' : `${s.holdings} / ${s.largestWeight == null ? '—' : pct(s.largestWeight, 1)}`) },
  ]
  return (
    <div className="space-y-3">
      <Warnings items={data.warnings} />
      {dates.length > 1 ? (
        <Card><CardContent className="pt-4">
          <Legend items={chartSeries.map((s) => ({ name: s.name, color: s.color, dashed: s.dashed }))} />
          <TimeSeriesChart dates={dates} series={chartSeries} format={(v) => pct(v, 1)} zeroLine />
        </CardContent></Card>
      ) : (
        <p className="rounded-lg border border-dashed px-4 py-6 text-center text-sm text-muted-foreground">
          {data.baselineAtUtc ? 'Not enough valued days since the baseline for a chart yet. The record grows with every trading day.' : 'Switch Matrix Pilot on to start its live record.'}
        </p>
      )}
      <div className="overflow-x-auto rounded-xl border">
        <Table>
          <TableHeader><TableRow><TableHead>{data.currency}, from {data.start ?? '—'}</TableHead>{series.map((s) => <TableHead key={s.key} className="text-right">{s.label}</TableHead>)}</TableRow></TableHeader>
          <TableBody>{rows.map((r) => <TableRow key={r.label}><TableCell>{r.label}</TableCell>{series.map((s) => <TableCell key={s.key} className="text-right tabular">{r.render(s)}</TableCell>)}</TableRow>)}</TableBody>
        </Table>
      </div>
      {data.monthly.length > 0 && (
        <div className="overflow-x-auto rounded-xl border">
          <Table>
            <TableHeader><TableRow><TableHead>Month</TableHead><TableHead className="text-right">Matrix Pilot</TableHead>{data.other && <TableHead className="text-right">{data.other.label}</TableHead>}<TableHead className="text-right">Benchmark</TableHead></TableRow></TableHeader>
            <TableBody>{data.monthly.map((m) => (
              <TableRow key={m.month}><TableCell>{m.month}</TableCell><TableCell className="text-right tabular">{pct(m.pilot, 2, true)}</TableCell>
                {data.other && <TableCell className="text-right tabular">{pct(m.other, 2, true)}</TableCell>}<TableCell className="text-right tabular">{pct(m.benchmark, 2, true)}</TableCell></TableRow>
            ))}</TableBody>
          </Table>
        </div>
      )}
      {data.timeline.length > 0 && (
        <SearchableTable label="timeline entries" caption="Matrix Pilot timeline" rows={data.timeline} rowKey={(t) => `${t.atUtc}-${t.kind}-${t.cycleId ?? ''}`} dense maxRows={8}
          placeholder="Search timeline" matches={(t, q) => t.text.toLowerCase().includes(q)} initialSort={{ key: 'at', dir: 'desc' }} columns={[
            { key: 'at', header: 'When', cell: (t) => dateTime(t.atUtc), sortValue: (t) => t.atUtc },
            { key: 'text', header: 'Event', cell: (t) => t.text },
          ]} />
      )}
      <p className="text-xs text-muted-foreground">{data.methodology}</p>
    </div>
  )
}

// ---------------------------------------------------------------- scorecard

function ScorecardSection({ pilot }: { pilot: Pilot }) {
  const card = usePilotScorecard(pilot.portfolioId, true)
  type Row = NonNullable<typeof card.data>['rows'][number]
  const columns: Column<Row>[] = [
    { key: 'at', header: 'Decided', cell: (r) => dateTime(r.decidedAtUtc), sortValue: (r) => r.decidedAtUtc },
    { key: 'action', header: 'Decision', cell: (r) => `${r.action} · ${r.outcome}`, sortValue: (r) => r.action },
    { key: 'approach', header: 'Approach / confidence', cell: (r) => `${r.approach} / ${r.confidence}` },
    { key: 'targets', header: 'Targets', cell: (r) => <span className="line-clamp-2 max-w-xs">{r.targets.map((t) => `${t.symbol} ${pct(t.weight, 0)}`).join(', ') || '—'}</span> },
    { key: 'd1', header: 'Portfolio +1D', align: 'right', cell: (r) => pct(r.portfolioAfter1D, 2, true), sortValue: (r) => r.portfolioAfter1D },
    { key: 'w1', header: '+1W', align: 'right', cell: (r) => pct(r.portfolioAfter1W, 2, true), sortValue: (r) => r.portfolioAfter1W },
    { key: 'm1', header: '+1M', align: 'right', cell: (r) => pct(r.portfolioAfter1M, 2, true), sortValue: (r) => r.portfolioAfter1M },
    { key: 'b1', header: 'Benchmark +1M', align: 'right', cell: (r) => pct(r.benchmarkAfter1M, 2, true), sortValue: (r) => r.benchmarkAfter1M },
  ]
  return (
    <ResultSection title="Decision scorecard" hint="Each decision as recorded at the time, with what happened afterwards (measured from stored prices). A blank means that period has not passed yet.">
      {card.error ? <ErrorState error={card.error} /> : !card.data ? <LoadingBlock height="h-32" /> : (
        <>
          <SearchableTable label="decisions" rows={card.data.rows} rowKey={(r) => r.cycleId} columns={columns} placeholder="Search decisions" dense maxRows={10}
            matches={(r, q) => r.targets.some((t) => t.symbol.toLowerCase().includes(q)) || r.approach.includes(q)} initialSort={{ key: 'at', dir: 'desc' }}
            empty="No decision to score yet." />
          <p className="text-xs text-muted-foreground">{card.data.methodology}</p>
        </>
      )}
    </ResultSection>
  )
}

// ---------------------------------------------------------------- mandate

function MandateSection({ overview, pilot }: { overview: PilotOverview; pilot: Pilot }) {
  const versions = usePilotMandates(pilot.portfolioId, true)
  const [editing, setEditing] = useState(false)
  return (
    <ResultSection title="Mandate" hint="The limits the AI works within. Only you can change them; every change is a new version and the old one is kept. The AI can only ask (see its requests above).">
      <MandateSummary mandate={pilot.mandate} />
      <div className="flex flex-wrap gap-2">
        <Button variant="outline" onClick={() => setEditing(true)} disabled={pilot.displayStatus === 'RUNNING' || !overview.portfolioActive}>Change mandate</Button>
      </div>
      {versions.data && versions.data.length > 0 && (
        <SearchableTable label="mandate versions" rows={versions.data} rowKey={(v) => String(v.version)} dense maxRows={6} placeholder="Search versions"
          matches={(v, q) => (v.reason ?? '').toLowerCase().includes(q)} initialSort={{ key: 'v', dir: 'desc' }} columns={[
            { key: 'v', header: 'Version', cell: (v) => v.version, sortValue: (v) => v.version },
            { key: 'at', header: 'Created', cell: (v) => dateTime(v.createdAtUtc), sortValue: (v) => v.createdAtUtc },
            { key: 'reason', header: 'Reason', cell: (v) => v.reason ?? '—' },
            { key: 'changes', header: 'Changes', cell: (v) => <span className="line-clamp-3 max-w-lg text-xs">{v.changes.map((c) => `${c.field}: ${c.before} → ${c.after}`).join('; ') || 'initial'}</span> },
          ]} />
      )}
      {editing && <MandateDialog overview={overview} pilot={pilot} onClose={() => setEditing(false)} />}
    </ResultSection>
  )
}

function MandateSummary({ mandate: m }: { mandate: PilotMandate }) {
  const limits = (r: Record<string, number>) => Object.entries(r).map(([k, v]) => `${k} ${pct(v, 0)}`).join(', ') || 'none'
  const rows: [string, string][] = [
    ['Universe', `${m.indexes.length ? `Indexes ${m.indexes.join(', ')}` : ''}${m.indexes.length && m.universe.length ? ' + ' : ''}${m.universe.length ? `${m.universe.length} symbol(s)` : ''}`],
    ['Excluded', m.excludedSymbols.join(', ') || 'none'],
    ['Position weight', `${pct(m.minPositionWeight, 1)} – ${pct(m.maxPositionWeight, 1)}`],
    ['Holdings', `${m.minHoldings} – ${m.maxHoldings}`],
    ['Cash', `${pct(m.minCashWeight, 0)} – ${pct(m.maxCashWeight, 0)}`],
    ['Turnover per decision', `at most ${pct(m.maxTurnover, 0)}`],
    ['Minimum trade', m.minTradeValue.toLocaleString()],
    ['Volatility limit', m.maxVolatility == null ? 'none' : pct(m.maxVolatility, 0)],
    ['Drawdown pause', pct(m.drawdownPauseThreshold, 0)],
    ['Sector / currency / country limits', `${limits(m.sectorLimits)} / ${limits(m.currencyLimits)} / ${limits(m.countryLimits)}`],
    ['Benchmark', m.benchmark ?? "the portfolio's"],
    ['Short selling / leverage', 'not allowed / not allowed'],
  ]
  return (
    <dl className="grid gap-x-6 gap-y-1 text-sm sm:grid-cols-2">
      {rows.map(([k, v]) => <div key={k} className="flex justify-between gap-3 border-b py-1"><dt className="text-muted-foreground">{k}</dt><dd className="text-right">{v}</dd></div>)}
    </dl>
  )
}

// ---------------------------------------------------------------- audit trail

function AuditSection({ pilot }: { pilot: Pilot }) {
  const events = usePilotEvents(pilot.portfolioId, true)
  const columns: Column<PilotEvent>[] = [
    { key: 'at', header: 'When', cell: (e) => dateTime(e.atUtc), sortValue: (e) => e.atUtc },
    { key: 'actor', header: 'Who', cell: (e) => (e.actor === 'MatrixPilot' ? 'Matrix Pilot' : e.actor === 'Owner' ? 'You' : 'System'), sortValue: (e) => e.actor },
    { key: 'action', header: 'Action', cell: (e) => e.action, sortValue: (e) => e.action },
    { key: 'status', header: 'Status change', cell: (e) => (e.previousStatus && e.newStatus && e.previousStatus !== e.newStatus ? `${e.previousStatus} → ${e.newStatus}` : '—') },
    { key: 'reason', header: 'Reason', cell: (e) => <span className="line-clamp-2 max-w-md">{e.reason ?? '—'}</span> },
  ]
  return (
    <ResultSection title="Audit trail" hint="Every switch-on, kill switch, pause, resume, mandate change and cycle outcome, with who caused it and why." collapsible>
      {events.error ? <ErrorState error={events.error} /> : !events.data ? <LoadingBlock height="h-24" /> : (
        <SearchableTable label="events" rows={events.data.items} rowKey={(e) => String(e.id)} columns={columns} dense placeholder="Search events"
          matches={(e, q) => `${e.action} ${e.reason ?? ''}`.toLowerCase().includes(q)} initialSort={{ key: 'at', dir: 'desc' }} maxRows={10} />
      )}
    </ResultSection>
  )
}

// ================================================================ actions and dialogs

function PilotActions({ overview, pilot }: { overview: PilotOverview; pilot: Pilot }) {
  const [dialog, setDialog] = useState<'enable' | 'resume' | 'kill' | 'off' | 'run' | null>(null)
  const active = pilot.status === 'Active'
  const stopped = pilot.status === 'Paused' || pilot.status === 'KillSwitched'
  return (
    <>
      {(active || pilot.status === 'Paused') && (
        <Button variant="destructive" onClick={() => setDialog('kill')} className="font-semibold">Kill switch</Button>
      )}
      {active && <Button variant="outline" disabled={pilot.displayStatus === 'RUNNING' || !overview.portfolioActive} onClick={() => setDialog('run')}>Run decision now</Button>}
      {pilot.status === 'Disabled' && <Button onClick={() => setDialog('enable')} disabled={!overview.configuredModel.available || !overview.portfolioActive}>Switch Matrix Pilot on</Button>}
      {stopped && <Button onClick={() => setDialog('resume')} disabled={!overview.configuredModel.available || !overview.portfolioActive}>Resume</Button>}
      {pilot.status !== 'Disabled' && <Button variant="ghost" onClick={() => setDialog('off')}>Switch off</Button>}
      {(dialog === 'enable' || dialog === 'resume') && <ConfirmActivation overview={overview} pilot={pilot} mode={dialog} onClose={() => setDialog(null)} />}
      {(dialog === 'kill' || dialog === 'off') && <StopDialog pilot={pilot} mode={dialog} onClose={() => setDialog(null)} />}
      {dialog === 'run' && <RunNowDialog pilot={pilot} onClose={() => setDialog(null)} />}
    </>
  )
}

/** The explicit confirmation of autonomous management (spec: never by accident). */
function ConfirmActivation({ overview, pilot, mode, onClose }: { overview: PilotOverview; pilot: Pilot; mode: 'enable' | 'resume'; onClose: () => void }) {
  const enable = useEnablePilot(pilot.portfolioId)
  const resume = useResumePilot(pilot.portfolioId)
  const [understood, setUnderstood] = useState(false)
  const m = overview.configuredModel
  const action = mode === 'enable' ? enable : resume
  const submit = () => action.mutate({ confirm: true, provider: m.provider, model: m.model }, {
    onSuccess: () => { toast.success(mode === 'enable' ? 'Matrix Pilot is on.' : 'Matrix Pilot resumed.'); onClose() },
    onError: (e) => toast.error(errorMessage(e)),
  })
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>{mode === 'enable' ? 'Enable autonomous AI portfolio management' : 'Resume autonomous AI portfolio management'}</DialogTitle>
          <DialogDescription>Paper money only – no real order is ever sent. Read what you are confirming.</DialogDescription>
        </DialogHeader>
        <dl className="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1 text-sm">
          <dt className="text-muted-foreground">Portfolio</dt><dd>{overview.portfolioName} ({overview.baseCurrency})</dd>
          <dt className="text-muted-foreground">AI</dt><dd>{modelText(m, m.isTestModel)}, effort {pilot.effort}</dd>
          <dt className="text-muted-foreground">Decision frequency</dt><dd>{pilot.frequency}</dd>
          <dt className="text-muted-foreground">Execution</dt><dd>Automatic (simulated) within the mandate, version {pilot.mandateVersion}</dd>
        </dl>
        <div className="grid gap-3 text-sm sm:grid-cols-2">
          <div><p className="font-medium">The AI may</p><ul className="list-disc pl-5 text-muted-foreground"><li>select securities</li><li>change weights</li><li>rebalance</li><li>create simulated transactions</li></ul></div>
          <div><p className="font-medium">The platform will</p><ul className="list-disc pl-5 text-muted-foreground"><li>validate every allocation</li><li>enforce the mandate and permissions</li><li>record every decision</li><li>keep an audit trail</li><li>let you stop it at once</li></ul></div>
        </div>
        <label className="flex items-start gap-2 text-sm">
          <Checkbox checked={understood} onCheckedChange={(c) => setUnderstood(c === true)} aria-label="I understand" />
          <span>I understand that an AI model will manage this paper portfolio on its own until I stop it.</span>
        </label>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button onClick={submit} disabled={!understood || action.isPending}>{action.isPending && <MatrixLoader />}{mode === 'enable' ? 'Switch Matrix Pilot on' : 'Resume Matrix Pilot'}</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function StopDialog({ pilot, mode, onClose }: { pilot: Pilot; mode: 'kill' | 'off'; onClose: () => void }) {
  const kill = useKillPilot(pilot.portfolioId)
  const off = useDisablePilot(pilot.portfolioId)
  const [reason, setReason] = useState('')
  const action = mode === 'kill' ? kill : off
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{mode === 'kill' ? 'Kill switch' : 'Switch Matrix Pilot off'}</DialogTitle>
          <DialogDescription>
            {mode === 'kill'
              ? 'Stops Matrix Pilot at once: no new decision or trade. A cycle in progress checks again before booking and stops. Booked trades stay in the ledger. Only you can resume.'
              : 'Switches Matrix Pilot off. Its journal and live record are kept; switching it on again needs a new confirmation.'}
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-1">
          <Label htmlFor="stop-reason">Reason (recorded in the audit trail)</Label>
          <Input id="stop-reason" value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} placeholder="optional" />
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button variant="destructive" disabled={action.isPending}
            onClick={() => action.mutate(reason, { onSuccess: () => { toast.success(mode === 'kill' ? 'Matrix Pilot stopped.' : 'Matrix Pilot switched off.'); onClose() }, onError: (e) => toast.error(errorMessage(e)) })}>
            {action.isPending && <MatrixLoader />}{mode === 'kill' ? 'Stop Matrix Pilot now' : 'Switch off'}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

function RunNowDialog({ pilot, onClose }: { pilot: Pilot; onClose: () => void }) {
  const run = useRunPilot(pilot.portfolioId)
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Run this period's decision now</DialogTitle>
          <DialogDescription>
            Matrix Pilot decides once per {pilot.frequency === 'Daily' ? 'day' : pilot.frequency === 'Weekly' ? 'week' : 'month'}. Running it now uses this period's decision; the
            scheduled one is then skipped. It may book simulated trades within the mandate, and the model calls are billed by the provider.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button disabled={run.isPending} onClick={() => run.mutate(undefined, { onSuccess: () => { toast.success('Decision cycle started.'); onClose() }, onError: (e) => toast.error(errorMessage(e)) })}>
            {run.isPending && <MatrixLoader />}Run decision now
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

// ---------------------------------------------------------------- setup and mandate form

function SetupCard({ overview }: { overview: PilotOverview }) {
  const create = useCreatePilot(overview.portfolioId)
  const [form, setForm] = useState(() => toForm(overview.suggestedMandate, 'Weekly', 'high'))
  if (!overview.portfolioActive) return <EmptyState title="This portfolio is archived" description="Restore it to set up Matrix Pilot." />
  const submit = () => {
    const parsed = fromForm(form)
    if ('error' in parsed) return toast.error(parsed.error)
    create.mutate({ mandate: parsed.mandate, frequency: form.frequency, effort: form.effort }, {
      onSuccess: () => toast.success('Matrix Pilot is set up. Switch it on when you are ready.'),
      onError: (e) => toast.error(errorMessage(e)),
    })
  }
  return (
    <div className="space-y-4">
      <ModelNote model={overview.configuredModel} />
      <Card>
        <CardHeader><CardTitle>Set up Matrix Pilot for {overview.portfolioName}</CardTitle></CardHeader>
        <CardContent className="space-y-4">
          <p className="text-sm text-muted-foreground">
            Define the mandate: where the AI may invest and the limits it must respect. Setting up does not switch it on – that is a separate, confirmed step.
            Its live record starts when you switch it on; the portfolio's earlier history is never counted as Matrix Pilot's.
          </p>
          <MandateForm form={form} onChange={setForm} />
          <Button onClick={submit} disabled={create.isPending}>{create.isPending && <MatrixLoader />}Set up Matrix Pilot</Button>
        </CardContent>
      </Card>
    </div>
  )
}

function MandateDialog({ overview, pilot, onClose }: { overview: PilotOverview; pilot: Pilot; onClose: () => void }) {
  const update = useUpdatePilotMandate(pilot.portfolioId)
  const [form, setForm] = useState(() => toForm(pilot.mandate, pilot.frequency, pilot.effort))
  const [reason, setReason] = useState('')
  const [adopt, setAdopt] = useState(false)
  const submit = () => {
    const parsed = fromForm(form)
    if ('error' in parsed) return toast.error(parsed.error)
    update.mutate({ mandate: parsed.mandate, frequency: form.frequency, effort: form.effort, reason, confirm: true, adoptConfiguredModel: adopt }, {
      onSuccess: () => { toast.success('New mandate version saved.'); onClose() },
      onError: (e) => toast.error(errorMessage(e)),
    })
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="max-h-[90dvh] overflow-y-auto sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle>Change the mandate</DialogTitle>
          <DialogDescription>Saving creates version {pilot.mandateVersion + 1}; version {pilot.mandateVersion} is kept with the changed fields. Applies from the next decision.</DialogDescription>
        </DialogHeader>
        <MandateForm form={form} onChange={setForm} />
        {!pilot.modelMatchesConfiguration && (
          <label className="flex items-start gap-2 text-sm">
            <Checkbox checked={adopt} onCheckedChange={(c) => setAdopt(c === true)} aria-label="Adopt the configured model" />
            <span>Adopt the configured model {modelText(overview.configuredModel)} (decisions keep the model that made them).</span>
          </label>
        )}
        <div className="space-y-1">
          <Label htmlFor="mandate-reason">Reason for the change</Label>
          <Input id="mandate-reason" value={reason} maxLength={500} onChange={(e) => setReason(e.target.value)} />
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button onClick={submit} disabled={update.isPending}>{update.isPending && <MatrixLoader />}Confirm the new mandate</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

interface MandateFormState {
  universe: string; indexes: string; excluded: string; maxPos: string; minPos: string; minHoldings: string; maxHoldings: string
  minCash: string; maxCash: string; maxTurnover: string; minTrade: string; maxVol: string; drawdown: string
  sectors: string; currencies: string; countries: string; benchmark: string; frequency: PilotFrequency; effort: string
}

const p100 = (v: number) => String(Math.round(v * 10000) / 100)
const limitsText = (r: Record<string, number>) => Object.entries(r).map(([k, v]) => `${k}=${p100(v)}`).join('\n')

function toForm(m: PilotMandate, frequency: PilotFrequency, effort: string): MandateFormState {
  return {
    universe: m.universe.join(', '), indexes: m.indexes.join(', '), excluded: m.excludedSymbols.join(', '), maxPos: p100(m.maxPositionWeight),
    minPos: p100(m.minPositionWeight), minHoldings: String(m.minHoldings), maxHoldings: String(m.maxHoldings), minCash: p100(m.minCashWeight),
    maxCash: p100(m.maxCashWeight), maxTurnover: p100(m.maxTurnover), minTrade: String(m.minTradeValue), maxVol: m.maxVolatility == null ? '' : p100(m.maxVolatility),
    drawdown: p100(m.drawdownPauseThreshold), sectors: limitsText(m.sectorLimits), currencies: limitsText(m.currencyLimits), countries: limitsText(m.countryLimits),
    benchmark: m.benchmark ?? '', frequency, effort,
  }
}

/** Parses the form; the server validates the mandate again (and rejects, never repairs). */
function fromForm(f: MandateFormState): { mandate: PilotMandate } | { error: string } {
  const list = (s: string) => s.split(/[\s,;]+/).map((x) => x.trim()).filter(Boolean)
  const frac = (s: string, name: string) => {
    const v = Number(s.replace('%', ''))
    if (!Number.isFinite(v)) throw new Error(`${name} must be a number (percent).`)
    return v / 100
  }
  const limits = (s: string, name: string) => Object.fromEntries(s.split('\n').map((l) => l.trim()).filter(Boolean).map((l) => {
    const [k, v] = l.split('=')
    if (!k || v === undefined) throw new Error(`${name}: write one “Name=percent” per line.`)
    return [k.trim(), frac(v.trim(), name)]
  }))
  try {
    return {
      mandate: {
        universe: list(f.universe).map((s) => s.toUpperCase()), indexes: list(f.indexes), excludedSymbols: list(f.excluded).map((s) => s.toUpperCase()),
        maxPositionWeight: frac(f.maxPos, 'Maximum position'), minPositionWeight: frac(f.minPos, 'Minimum position'),
        minHoldings: Number(f.minHoldings), maxHoldings: Number(f.maxHoldings), minCashWeight: frac(f.minCash, 'Minimum cash'), maxCashWeight: frac(f.maxCash, 'Maximum cash'),
        maxTurnover: frac(f.maxTurnover, 'Maximum turnover'), minTradeValue: Number(f.minTrade), maxVolatility: f.maxVol.trim() ? frac(f.maxVol, 'Maximum volatility') : null,
        drawdownPauseThreshold: frac(f.drawdown, 'Drawdown pause'), sectorLimits: limits(f.sectors, 'Sector limits'), currencyLimits: limits(f.currencies, 'Currency limits'),
        countryLimits: limits(f.countries, 'Country limits'), benchmark: f.benchmark.trim() || null,
      },
    }
  } catch (e) {
    return { error: (e as Error).message }
  }
}

const textarea = 'min-h-16 w-full rounded-lg border border-input bg-transparent px-3 py-2 text-sm shadow-xs outline-none focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50'

function MandateForm({ form, onChange }: { form: MandateFormState; onChange: (f: MandateFormState) => void }) {
  const set = (k: keyof MandateFormState) => (e: { target: { value: string } }) => onChange({ ...form, [k]: e.target.value })
  const field = (k: keyof MandateFormState, label: string, hint?: string) => (
    <div className="space-y-1">
      <Label htmlFor={`m-${k}`}><InfoTip of={label}>{hint}</InfoTip></Label>
      <Input id={`m-${k}`} value={String(form[k])} onChange={set(k)} inputMode="decimal" />
    </div>
  )
  const area = (k: keyof MandateFormState, label: string, placeholder: string, hint?: string) => (
    <div className="space-y-1">
      <Label htmlFor={`m-${k}`}><InfoTip of={label}>{hint}</InfoTip></Label>
      <textarea id={`m-${k}`} className={textarea} value={String(form[k])} onChange={set(k)} placeholder={placeholder} />
    </div>
  )
  const freq = [{ value: 'Daily', label: 'Daily' }, { value: 'Weekly', label: 'Weekly' }, { value: 'Monthly', label: 'Monthly' }]
  const efforts = ['low', 'medium', 'high', 'xhigh', 'max'].map((e) => ({ value: e, label: e }))
  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2">
        {area('universe', 'Allowed symbols', 'AAPL, MSFT, …', 'Symbols the AI may hold (in addition to index members).')}
        {area('indexes', 'Allowed indexes', 'e.g. SP500', 'Every current member of these indexes is investable.')}
        {area('excluded', 'Excluded symbols', 'e.g. XOM', 'Never held, whatever the AI proposes.')}
        <div className="space-y-1">
          <Label>Decision frequency and effort</Label>
          <div className="flex gap-2">
            <Select value={form.frequency} onValueChange={(v) => v && onChange({ ...form, frequency: v as PilotFrequency })} items={freq}>
              <SelectTrigger aria-label="Decision frequency" className="w-36"><SelectValue /></SelectTrigger>
              <SelectContent>{freq.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
            </Select>
            <Select value={form.effort} onValueChange={(v) => v && onChange({ ...form, effort: String(v) })} items={efforts}>
              <SelectTrigger aria-label="Model effort" className="w-28"><SelectValue /></SelectTrigger>
              <SelectContent>{efforts.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
            </Select>
          </div>
          <p className="text-xs text-muted-foreground">Higher effort means more thorough model reasoning and higher cost per decision.</p>
        </div>
      </div>
      <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
        {field('maxPos', 'Max position %')}
        {field('minPos', 'Min position %')}
        {field('minHoldings', 'Min holdings')}
        {field('maxHoldings', 'Max holdings')}
        {field('minCash', 'Min cash %')}
        {field('maxCash', 'Max cash %')}
        {field('maxTurnover', 'Max turnover %', 'One-way turnover per decision, once the portfolio holds positions.')}
        {field('minTrade', 'Min trade value', 'Smallest order, in the portfolio currency.')}
        {field('maxVol', 'Max volatility %', 'Ceiling for the proposal\'s ex-ante annualized volatility (empty = none).')}
        {field('drawdown', 'Drawdown pause %', 'Matrix Pilot pauses itself when its live record falls this far from its peak.')}
        {field('benchmark', 'Benchmark', 'Empty = the portfolio\'s benchmark.')}
      </div>
      <div className="grid gap-3 sm:grid-cols-3">
        {area('sectors', 'Sector limits %', 'Energy=10')}
        {area('currencies', 'Currency limits %', 'EUR=30')}
        {area('countries', 'Country limits %', 'US=70')}
      </div>
      <p className="text-xs text-muted-foreground">Long-only, no leverage: the platform supports nothing else. The server rejects an impossible mandate instead of adjusting it.</p>
    </div>
  )
}

// ================================================================ Arena (portfolio › Comparison)

/**
 * Matrix Pilot inside a portfolio's Arena tab: its forward-only live record against another portfolio and the benchmark when
 * this portfolio has a pilot; otherwise links to the user's pilots so this portfolio can be compared with them there.
 */
export function PilotInArena({ portfolioId }: { portfolioId: string }) {
  const overview = usePilot(portfolioId)
  const home = usePilotHome()
  const pilot = overview.data?.pilot
  if (pilot?.baseline) {
    return (
      <div className="mb-8 space-y-2">
        <LiveRecordSection pilot={pilot} title="Live record: Matrix Pilot" />
        <Button size="sm" variant="outline" render={<Link to={`/research/pilot/${portfolioId}`} />}>Open Matrix Pilot</Button>
      </div>
    )
  }
  const others = home.data?.portfolios.filter((p) => p.pilotStatus && p.portfolioId !== portfolioId) ?? []
  if (others.length === 0) return null
  return (
    <p className="mb-6 text-sm text-muted-foreground">
      Matrix Pilot runs on {others.map((p, i) => (
        <span key={p.portfolioId}>{i > 0 && ', '}<Link className="font-medium text-foreground hover:underline" to={`/research/pilot/${p.portfolioId}`}>{p.name}</Link></span>
      ))}. Its live record can be compared with this portfolio there (Live record vs another portfolio).
    </p>
  )
}
