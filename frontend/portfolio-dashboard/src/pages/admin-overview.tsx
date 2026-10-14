import { Link } from 'react-router'
import { AlertTriangle, CheckCircle2, KeyRound, Loader2, RefreshCw, Users } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Progress } from '@/components/ui/progress'
import { DataTable, type Column } from '@/components/data-table'
import { EmptyState, ErrorState, InfoTip, LoadingBlock, MetricCard, PageHeader } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { useAdminOverview, useStartQuoteRefresh } from '@/lib/queries'
import { age } from '@/lib/quote-status'
import type { AdminOverview, QuoteRefreshRun } from '@/lib/types'
import { dateTime, num } from '@/lib/format'

type RecentRun = AdminOverview['recentRuns'][number]
type IndexHealth = AdminOverview['marketData']['indices'][number]

/** Administrator dashboard: accounts, genuine analysis activity and market-data health (no secrets, no results). */
export function AdminOverviewPage() {
  const overview = useAdminOverview()
  const startRefresh = useStartQuoteRefresh()

  if (overview.error) return <ErrorState error={overview.error} onRetry={() => overview.refetch()} />
  if (!overview.data) return <LoadingBlock height="h-96" />
  const o = overview.data

  const refreshNow = async () => {
    try {
      const run = await startRefresh.mutateAsync()
      toast.success(run.reused ? 'A price refresh is already running' : 'Price refresh started', { description: 'Quotes for all markets are refreshed in the background.' })
    } catch (e) {
      toast.error('Could not start the refresh', { description: errorMessage(e) })
    }
  }

  const runColumns: Column<RecentRun>[] = [
    { key: 'kind', header: 'Type', cell: (r) => <Badge variant="outline">{r.kind}</Badge> },
    { key: 'name', header: 'Run', cell: (r) => <span className="block max-w-56 truncate xl:max-w-72" title={r.name}>{r.name}</span> },
    { key: 'owner', header: 'User', cell: (r) => <div><div className="text-sm">{r.ownerName}</div><div className="text-[11px] text-muted-foreground">{r.ownerEmail}</div></div> },
    { key: 'status', header: 'Status', cell: (r) => <StatusBadge status={r.status} /> },
    { key: 'created', header: 'Started', cell: (r) => <span className="text-xs">{dateTime(r.createdAtUtc)}</span> },
  ]
  const indexColumns: Column<IndexHealth>[] = [
    { key: 'name', header: 'Universe', cell: (i) => <div><div className="font-medium">{i.name}</div><div className="text-[11px] text-muted-foreground">{i.code}</div></div> },
    { key: 'members', header: 'Members', align: 'right', cell: (i) => <span className="tabular">{i.members}</span> },
    {
      key: 'quotes',
      header: 'Price retrieved today',
      cell: (i) => (
        <div className="flex min-w-40 items-center gap-2">
          <Progress value={i.members ? (100 * i.withQuoteToday) / i.members : 0} className="h-1.5 w-24" aria-label={`${i.withQuoteToday} of ${i.members} with a price retrieved today`} />
          <span className="text-xs tabular">{i.withQuoteToday}/{i.members}</span>
        </div>
      ),
    },
    { key: 'history', header: 'With daily history', align: 'right', cell: (i) => <span className="tabular">{i.withHistory}</span> },
    { key: 'verified', header: 'List verified', cell: (i) => <span className="text-xs">{i.verifiedOn ?? '—'}</span> },
  ]

  return (
    <>
      <PageHeader
        title="Admin overview"
        description="Accounts, analysis activity and market-data health. Counts include genuine accounts only: automated-test accounts (reserved .test domain) are excluded and listed separately."
        actions={<Button size="sm" variant="outline" render={<Link to="/admin/users" />}><Users /> Manage users</Button>}
      />

      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
        <MetricCard label="Pending applications" value={num(o.accounts.pending, 0)} sub={o.accounts.pending > 0 ? 'Waiting for your decision' : 'Nothing to review'} />
        <MetricCard label="Approved accounts" value={num(o.accounts.approved, 0)} sub={`${o.accounts.administrators} administrator(s) · ${o.accounts.suspended} suspended · ${o.accounts.rejected} rejected`} />
        <MetricCard label="Optimization runs" value={num(o.activity.optimizationRuns, 0)} sub={`${o.activity.optimizationRunsLast30Days} in the last 30 days`} />
        <MetricCard label="ML experiments" value={num(o.activity.mlExperiments, 0)} sub={`${o.activity.mlExperimentsActive} running · ${o.activity.mlExperimentsFailed} failed`} />
      </div>
      {o.accounts.testAccounts > 0 && (
        <p className="mt-2 flex items-center gap-1.5 text-xs text-warn">
          <AlertTriangle className="size-3.5" aria-hidden />
          {o.accounts.testAccounts} automated-test account(s) exist in this database. They are excluded above; remove them with
          <code className="rounded bg-muted px-1">PortfolioOptimizer.Api purge-test-data --apply</code> (see docs/testing.md).
        </p>
      )}

      <div className="mt-6 grid gap-4 xl:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Recent analyses</CardTitle>
            <CardDescription>What ran, when and for whom – results stay private to their owner.</CardDescription>
          </CardHeader>
          <CardContent>
            <DataTable rows={o.recentRuns} columns={runColumns} rowKey={(r) => `${r.kind}-${r.id}`} dense caption="Recent analyses"
              empty={<EmptyState title="No analyses yet" description="Optimizations, ML experiments and comparisons appear here." />} />
            <p className="mt-2 text-xs text-muted-foreground">
              {o.activity.strategyComparisons} comparison(s), {o.activity.backtests} backtest(s) and {o.activity.portfolios} paper portfolio(s) in total.
            </p>
          </CardContent>
        </Card>

        <Card>
          <CardHeader className="flex flex-row items-start justify-between gap-2">
            <div>
              <CardTitle className="text-base">Data providers</CardTitle>
              <CardDescription>Active: {o.marketData.activeProvider}. Keys are never shown, only whether they are configured.</CardDescription>
            </div>
            <Button size="sm" variant="outline" onClick={refreshNow} disabled={startRefresh.isPending}>
              {startRefresh.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />} Refresh prices now
            </Button>
          </CardHeader>
          <CardContent className="space-y-3">
            <ul className="space-y-2">
              {o.marketData.providers.map((p) => (
                <li key={p.name} className="rounded-lg border px-3 py-2 text-sm">
                  <div className="flex flex-wrap items-center gap-2">
                    <span className="font-medium">{p.displayName}</span>
                    {p.name === o.marketData.activeProvider && <Badge>Active</Badge>}
                    {p.isSimulated && <Badge variant="outline">Simulated</Badge>}
                    {p.requiresApiKey && (
                      <Badge variant="outline" className={p.isConfigured ? 'border-gain/40 text-gain' : 'border-loss/40 text-loss'}>
                        <KeyRound className="size-3" /> {p.isConfigured ? 'Key configured' : 'No key'}
                      </Badge>
                    )}
                  </div>
                  <div className="mt-1 text-xs text-muted-foreground">
                    {p.rateLimit}
                    {p.remainingRequestsToday !== null && <> · <span className="font-medium text-foreground">{p.remainingRequestsToday} requests left today</span></>}
                  </div>
                </li>
              ))}
            </ul>
            <div>
              <h3 className="mb-1 text-sm font-medium">Recent price refreshes</h3>
              {o.marketData.recentQuoteRefreshes.length === 0
                ? <p className="text-xs text-muted-foreground">None yet. A refresh starts when an approved user signs in.</p>
                : <ul className="space-y-1">{o.marketData.recentQuoteRefreshes.map((r) => <RefreshRow key={r.id} run={r} />)}</ul>}
            </div>
            {o.marketData.lastHistorySync && (
              <p className="text-xs text-muted-foreground">
                Last daily-history download: {o.marketData.lastHistorySync.status}, {o.marketData.lastHistorySync.assetsTotal} instrument(s)
                {o.marketData.lastHistorySync.assetsFailed > 0 && `, ${o.marketData.lastHistorySync.assetsFailed} failed`} · {age(o.marketData.lastHistorySync.requestedAtUtc)}.
              </p>
            )}
          </CardContent>
        </Card>
      </div>

      <Card className="mt-4">
        <CardHeader>
          <CardTitle className="text-base">Market universes</CardTitle>
          <CardDescription>
            Constituents per index and how many have a price retrieved today (active provider). Update a constituent list under
            Admin settings → Instrument catalogue.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <DataTable rows={o.marketData.indices} columns={indexColumns} rowKey={(i) => i.code} dense caption="Market universes" />
        </CardContent>
      </Card>
      <p className="mt-3 text-xs text-muted-foreground">Generated {dateTime(o.generatedAtUtc)}; refreshes every 15 seconds.</p>
    </>
  )
}

function StatusBadge({ status }: { status: string }) {
  const tone = status === 'Succeeded' ? 'border-gain/40 text-gain' : status === 'Failed' || status.includes('Infeasible') ? 'border-loss/40 text-loss' : 'border-border'
  return <Badge variant="outline" className={tone}>{status}</Badge>
}

function RefreshRow({ run }: { run: QuoteRefreshRun }) {
  const running = run.status === 'Queued' || run.status === 'Running'
  return (
    <li className="flex items-start gap-2 text-xs">
      {running ? <Loader2 className="mt-0.5 size-3.5 animate-spin" aria-hidden /> : run.status === 'Succeeded'
        ? <CheckCircle2 className="mt-0.5 size-3.5 text-gain" aria-hidden />
        : <AlertTriangle className="mt-0.5 size-3.5 text-warn" aria-hidden />}
      <span>
        {run.trigger} · {age(run.requestedAtUtc)} · {running ? 'running' : `${run.updated} updated, ${run.unchanged} current, ${run.failed} without price`}
      </span>
      {run.message && <InfoTip label="Details">{run.message}</InfoTip>}
    </li>
  )
}
