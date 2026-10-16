import { useState } from 'react'
import { useNavigate } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Card, CardContent } from '@/components/ui/card'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { DataTable, Pager, type Column } from '@/components/data-table'
import { DataBadge, EmptyState, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useOptimizationHistory } from '@/lib/queries'
import type { OptimizationRunSummary, RunStatus } from '@/lib/types'
import { date, dateTime, num, objectiveLabels, pct } from '@/lib/format'

const statusOptions = [
  { value: 'all', label: 'All statuses' },
  { value: 'Succeeded', label: 'Succeeded' },
  { value: 'Infeasible', label: 'Infeasible' },
  { value: 'InvalidInput', label: 'Invalid input' },
  { value: 'Failed', label: 'Failed' },
]

/** Every stored mathematical optimization run of the signed-in user, across portfolios. */
export function HistoryPage() {
  const { portfolios } = usePortfolioContext()
  const [portfolioId, setPortfolioId] = useState('all')
  const [status, setStatus] = useState('all')
  const [page, setPage] = useState(1)
  const history = useOptimizationHistory(page, portfolioId === 'all' ? undefined : portfolioId, status === 'all' ? undefined : status as RunStatus)
  const navigate = useNavigate()
  const names = new Map(portfolios.map((p) => [p.id, p.name]))
  const portfolioOptions = [{ value: 'all', label: 'All portfolios' }, ...portfolios.map((p) => ({ value: p.id, label: p.name }))]

  const columns: Column<OptimizationRunSummary>[] = [
    { key: 'created', header: 'Run', sortValue: (r) => r.createdAtUtc, cell: (r) => <span className="text-xs">{dateTime(r.createdAtUtc)}</span> },
    { key: 'portfolio', header: 'Portfolio', cell: (r) => <span className="text-xs">{names.get(r.portfolioId) ?? '—'}</span> },
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
      <PageHeader
        title="Optimization history"
        description="Every completed mathematical optimization with its stored inputs, data window and results. Expected returns and volatilities are historical in-sample estimates, not forecasts."
      />
      <Card className="py-4">
        <CardContent className="space-y-3 px-4">
          <div className="flex flex-wrap gap-2">
            <Select value={portfolioId} onValueChange={(v) => { setPortfolioId(String(v)); setPage(1) }} items={portfolioOptions}>
              <SelectTrigger className="w-56" aria-label="Portfolio filter"><SelectValue /></SelectTrigger>
              <SelectContent>{portfolioOptions.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
            </Select>
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
                onRowClick={(r) => navigate(`/optimize/${r.id}`)}
                caption="Optimization runs"
                empty={<EmptyState title="No optimization runs yet" description="Select stocks and run an optimization; completed runs appear here." />}
              />
              <Pager page={page} totalPages={history.data?.totalPages ?? 1} onChange={setPage} />
            </>
          )}
        </CardContent>
      </Card>
    </>
  )
}
