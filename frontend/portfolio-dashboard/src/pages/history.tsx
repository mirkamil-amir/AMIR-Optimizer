import { useState } from 'react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent } from '@/components/ui/card'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { DataTable, Pager, type Column } from '@/components/data-table'
import { DataBadge, EmptyState, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { useMlExperiments, useOptimizationHistory } from '@/lib/queries'
import type { MlExperimentSummary, OptimizationRunSummary, RunStatus } from '@/lib/types'
import { date, dateTime, familyLabels, num, objectiveLabels, pct, targetLabels } from '@/lib/format'

const statusOptions = [
  { value: 'all', label: 'All statuses' },
  { value: 'Succeeded', label: 'Succeeded' },
  { value: 'Infeasible', label: 'Infeasible' },
  { value: 'InvalidInput', label: 'Invalid input' },
  { value: 'Failed', label: 'Failed' },
]

/**
 * Stored optimization runs. `/history`: every Math Optimization of the signed-in user across portfolios (filterable by
 * portfolio). Inside a portfolio (`/portfolios/:portfolioId/runs`): only that portfolio's Math Optimizations, followed by
 * its ML Optimizations – the page never leaves the portfolio.
 */
export function HistoryPage() {
  const { portfolios } = usePortfolioContext()
  const { portfolioId: scoped } = useParams()
  // `?portfolio=<id>` (older links) preselects that portfolio.
  const [params] = useSearchParams()
  const [chosen, setPortfolioId] = useState(() => params.get('portfolio') ?? 'all')
  const portfolioId = scoped ?? chosen
  const [status, setStatus] = useState('all')
  const [page, setPage] = useState(1)
  const history = useOptimizationHistory(page, portfolioId === 'all' ? undefined : portfolioId, status === 'all' ? undefined : status as RunStatus)
  const navigate = useNavigate()
  const names = new Map(portfolios.map((p) => [p.id, p.name]))
  const portfolioOptions = [{ value: 'all', label: 'All portfolios' }, ...portfolios.map((p) => ({ value: p.id, label: p.name }))]

  const columns: Column<OptimizationRunSummary>[] = [
    { key: 'created', header: 'Run', sortValue: (r) => r.createdAtUtc, cell: (r) => <span className="text-xs">{dateTime(r.createdAtUtc)}</span> },
    ...(scoped ? [] : [{ key: 'portfolio', header: 'Portfolio', cell: (r: OptimizationRunSummary) => <span className="text-xs">{names.get(r.portfolioId) ?? '—'}</span> }]),
    { key: 'objective', header: 'Objective', cell: (r) => objectiveLabels[r.objective] ?? r.objective },
    {
      key: 'status',
      header: 'Status',
      sortValue: (r) => r.status,
      cell: (r) => (
        <div className="flex flex-wrap items-center gap-1">
          <Badge variant="outline" className={r.status === 'Succeeded' ? 'border-gain/40 text-gain' : 'border-warn/50 text-warn'}>{r.status}</Badge>
          {r.usesSimulatedData && <DataBadge kind="simulated" />}
        </div>
      ),
    },
    { key: 'assets', header: 'Assets', align: 'right', sortValue: (r) => r.assetCount, cell: (r) => num(r.assetCount, 0) },
    { key: 'ret', header: 'Exp. return', align: 'right', sortValue: (r) => r.expectedReturn, cell: (r) => pct(r.expectedReturn) },
    { key: 'vol', header: 'Volatility', align: 'right', sortValue: (r) => r.volatility, cell: (r) => pct(r.volatility) },
    { key: 'sharpe', header: 'Sharpe', align: 'right', sortValue: (r) => r.sharpeRatio, cell: (r) => num(r.sharpeRatio) },
    {
      key: 'window',
      header: 'Estimation window',
      cell: (r) => <span className="text-xs text-muted-foreground">{r.periodStart ? `${date(r.periodStart)} – ${date(r.periodEnd)}` : '—'}</span>,
      className: 'hidden lg:table-cell',
    },
  ]

  return (
    <>
      {scoped ? (
        <PageHeader level={2} title="Optimization runs"
          info="Every Math and ML Optimization of this portfolio with its stored inputs and results."
          description="Expected returns and volatilities are historical in-sample estimates, not forecasts." />
      ) : (
        <PageHeader
          title="Optimization history"
          info="Every completed mathematical optimization with its stored inputs, data window and results."
          description="Expected returns and volatilities are historical in-sample estimates, not forecasts."
        />
      )}
      {scoped && <h3 className="mb-2 text-sm font-semibold">Math Optimizations</h3>}
      <Card className="py-4">
        <CardContent className="space-y-3 px-4">
          <div className="flex flex-wrap gap-2">
            {!scoped && (
              <Select value={portfolioId} onValueChange={(v) => { setPortfolioId(String(v)); setPage(1) }} items={portfolioOptions}>
                <SelectTrigger className="w-56" aria-label="Portfolio filter"><SelectValue /></SelectTrigger>
                <SelectContent>{portfolioOptions.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
              </Select>
            )}
            <Select value={status} onValueChange={(v) => { setStatus(String(v)); setPage(1) }} items={statusOptions}>
              <SelectTrigger className="w-44" aria-label="Status filter"><SelectValue /></SelectTrigger>
              <SelectContent>{statusOptions.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
            </Select>
          </div>
          {history.error ? <ErrorState error={history.error} /> : history.isLoading ? <LoadingBlock /> : (
            <>
              <DataTable
                rows={history.data?.items ?? []}
                columns={columns}
                rowKey={(r) => r.id}
                onRowClick={(r) => navigate(`/portfolios/${r.portfolioId}/optimize/${r.id}`)}
                caption={scoped ? 'Math Optimizations of this portfolio' : 'Optimization runs'}
                empty={<EmptyState title="No optimization runs yet" description="Select stocks and run an optimization; completed runs appear here." />}
              />
              <Pager page={page} totalPages={history.data?.totalPages ?? 1} onChange={setPage} />
            </>
          )}
        </CardContent>
      </Card>
      {scoped && <MlRuns portfolioId={scoped} />}
    </>
  )
}

/** ML Optimizations (experiments) of one portfolio; a row opens it in the portfolio's ML Optimization tab. */
function MlRuns({ portfolioId }: { portfolioId: string }) {
  const [page, setPage] = useState(1)
  const experiments = useMlExperiments(page, { portfolioId })
  const navigate = useNavigate()
  const columns: Column<MlExperimentSummary>[] = [
    { key: 'created', header: 'Run', sortValue: (e) => e.createdAtUtc, cell: (e) => <span className="text-xs">{dateTime(e.createdAtUtc)}</span> },
    { key: 'name', header: 'Name', cell: (e) => <span className="text-xs">{e.name}</span> },
    { key: 'model', header: 'Model', cell: (e) => <span className="text-xs">{familyLabels[e.modelFamily]} · {targetLabels[e.target]} · {e.horizonDays}d</span> },
    {
      key: 'status',
      header: 'Status',
      sortValue: (e) => e.status,
      cell: (e) => (
        <div className="flex flex-wrap items-center gap-1">
          <Badge variant="outline" className={e.status === 'Succeeded' ? 'border-gain/40 text-gain' : e.status === 'Failed' ? 'border-warn/50 text-warn' : ''}>{e.status}</Badge>
          {e.usesSimulatedData && <DataBadge kind="simulated" />}
        </div>
      ),
    },
    { key: 'assets', header: 'Stocks', align: 'right', sortValue: (e) => e.universeSize, cell: (e) => num(e.universeSize, 0) },
    { key: 'ic', header: 'Test rank corr.', align: 'right', sortValue: (e) => e.testRankCorrelation, cell: (e) => num(e.testRankCorrelation) },
  ]
  return (
    <>
      <h3 className="mt-6 mb-2 text-sm font-semibold">ML Optimizations</h3>
      <Card className="py-4">
        <CardContent className="space-y-3 px-4">
          {experiments.error ? <ErrorState error={experiments.error} /> : experiments.isLoading ? <LoadingBlock /> : (
            <>
              <DataTable
                rows={experiments.data?.items ?? []}
                columns={columns}
                rowKey={(e) => e.id}
                onRowClick={(e) => navigate(portfolioPath(portfolioId, `ml-optimize/${e.id}`))}
                caption="ML Optimizations of this portfolio"
                empty={<EmptyState title="No ML Optimizations yet" description="Train one under ML Optimization; it appears here with its out-of-sample test metrics." />}
              />
              <Pager page={page} totalPages={experiments.data?.totalPages ?? 1} onChange={setPage} />
            </>
          )}
        </CardContent>
      </Card>
    </>
  )
}
