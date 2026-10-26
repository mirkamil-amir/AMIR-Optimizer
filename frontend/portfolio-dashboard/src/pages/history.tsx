import { useMemo, useState } from 'react'
import { toast } from 'sonner'
import { Trash2 } from 'lucide-react'
import { useNavigate, useParams, useSearchParams } from 'react-router'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Card, CardContent } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { DataTable, type Column } from '@/components/data-table'
import { DataBadge, EmptyState, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { errorMessage } from '@/lib/api'
import { useDeleteMlExperiment, useDeleteOptimizationRun, useMlExperiments, useOptimizationHistory } from '@/lib/queries'
import type { MlExperimentSummary, OptimizationRunSummary } from '@/lib/types'
import { date, dateTime, familyLabels, num, objectiveLabels, pct, targetLabels } from '@/lib/format'

/** Rows visible at once; older runs scroll inside the list. */
const VISIBLE_ROWS = 10
/** The latest runs loaded into a list (the server's page limit for ML experiments is 100). */
const LOADED_RUNS = 100

const statusOptions = [
  { value: 'all', label: 'All statuses' },
  { value: 'Succeeded', label: 'Succeeded' },
  { value: 'Infeasible', label: 'Infeasible' },
  { value: 'InvalidInput', label: 'Invalid input' },
  { value: 'Failed', label: 'Failed' },
]
const mlStatusOptions = [
  { value: 'all', label: 'All statuses' },
  ...['Succeeded', 'Running', 'Queued', 'Failed', 'Cancelled'].map((s) => ({ value: s, label: s })),
]
const objectiveOptions = [{ value: 'all', label: 'All objectives' }, ...Object.entries(objectiveLabels).map(([value, label]) => ({ value, label }))]
const familyOptions = [{ value: 'all', label: 'All models' }, ...Object.entries(familyLabels).map(([value, label]) => ({ value, label }))]

const contains = (text: string, query: string) => text.toLowerCase().includes(query.trim().toLowerCase())

/**
 * Stored optimization runs. `/history`: every Math Optimization of the signed-in user across portfolios (filterable by
 * portfolio). Inside a portfolio (`/portfolios/:portfolioId/runs`): only that portfolio's Math Optimizations, followed by
 * its ML Optimizations – the page never leaves the portfolio. Each list shows the latest ten runs and scrolls for older ones.
 */
export function HistoryPage() {
  const { portfolios } = usePortfolioContext()
  const { portfolioId: scoped } = useParams()
  const [toDelete, setToDelete] = useState<OptimizationRunSummary | null>(null)
  const removeRun = useDeleteOptimizationRun()
  const archived = new Set(portfolios.filter((p) => p.status === 'Archived').map((p) => p.id))
  // `?portfolio=<id>` (older links) preselects that portfolio.
  const [params] = useSearchParams()
  const [chosen, setPortfolioId] = useState(() => params.get('portfolio') ?? 'all')
  const portfolioId = scoped ?? chosen
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState('all')
  const [objective, setObjective] = useState('all')
  const history = useOptimizationHistory(1, portfolioId === 'all' ? undefined : portfolioId, undefined, LOADED_RUNS)
  const navigate = useNavigate()
  const names = new Map(portfolios.map((p) => [p.id, p.name]))
  const portfolioOptions = [{ value: 'all', label: 'All portfolios' }, ...portfolios.map((p) => ({ value: p.id, label: p.name }))]
  const all = history.data?.items
  const rows = useMemo(() => (all ?? []).filter((r) =>
    (status === 'all' || r.status === status) &&
    (objective === 'all' || r.objective === objective) &&
    (search.trim() === '' || contains(`${objectiveLabels[r.objective] ?? r.objective} ${dateTime(r.createdAtUtc)}`, search)),
  ), [all, status, objective, search])

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
    {
      key: 'delete', header: <span className="sr-only">Delete</span>, align: 'right',
      cell: (r) => archived.has(r.portfolioId) ? null : <DeleteButton label={`Delete the ${objectiveLabels[r.objective] ?? r.objective} run of ${dateTime(r.createdAtUtc)}`} onClick={() => setToDelete(r)} />,
    },
  ]

  return (
    <>
      {scoped ? (
        <PageHeader level={2} titleHidden title="History"
          info="Every Math and ML Optimization of this portfolio with its stored inputs and results."
          description="Expected returns and volatilities are historical in-sample estimates, not forecasts." />
      ) : (
        <PageHeader
          title="History"
          info="Every completed mathematical optimization with its stored inputs, data window and results."
          description="Expected returns and volatilities are historical in-sample estimates, not forecasts."
        />
      )}
      {scoped && <h3 className="mb-2 text-sm font-semibold">Math Optimizations</h3>}
      <Card className="py-4">
        <CardContent className="space-y-3 px-4">
          <div className="flex flex-wrap gap-2">
            <SearchBox value={search} onChange={setSearch} label="Search Math Optimizations" />
            {!scoped && <Filter value={portfolioId} onChange={setPortfolioId} options={portfolioOptions} label="Portfolio filter" width="w-56" />}
            <Filter value={status} onChange={setStatus} options={statusOptions} label="Status filter" />
            <Filter value={objective} onChange={setObjective} options={objectiveOptions} label="Objective filter" width="w-52" />
          </div>
          {history.error ? <ErrorState error={history.error} /> : history.isLoading ? <LoadingBlock /> : (
            <>
              <DataTable
                rows={rows}
                columns={columns}
                rowKey={(r) => r.id}
                maxRows={VISIBLE_ROWS}
                onRowClick={(r) => navigate(`/portfolios/${r.portfolioId}/optimize/${r.id}`)}
                caption={scoped ? 'Math Optimizations of this portfolio' : 'Optimization runs'}
                empty={all?.length
                  ? <EmptyState title="No matching runs" description="Change the search or the filters." />
                  : <EmptyState title="No optimization runs yet" description="Select stocks and run an optimization; completed runs appear here." />}
              />
              <ListCount shown={rows.length} loaded={all?.length ?? 0} total={history.data?.totalCount ?? 0} />
            </>
          )}
        </CardContent>
      </Card>
      {scoped && <MlRuns portfolioId={scoped} archived={archived.has(scoped)} />}
      <ConfirmDelete
        open={!!toDelete}
        title={toDelete ? `Delete the ${objectiveLabels[toDelete.objective] ?? toDelete.objective} run of ${dateTime(toDelete.createdAtUtc)}?` : ''}
        description="The run and its weights are removed. Trade proposals made from it stay, and backtests and strategy comparisons keep their own copy of its settings. Holdings and transactions do not change. It cannot be undone."
        pending={removeRun.isPending}
        onClose={() => setToDelete(null)}
        onConfirm={async () => { if (toDelete) await removeRun.mutateAsync(toDelete.id) }}
      />
    </>
  )
}

/** ML Optimizations (experiments) of one portfolio; a row opens it in the portfolio's ML Optimization tab. */
function MlRuns({ portfolioId, archived }: { portfolioId: string; archived: boolean }) {
  const [toDelete, setToDelete] = useState<MlExperimentSummary | null>(null)
  const remove = useDeleteMlExperiment()
  const experiments = useMlExperiments(1, { portfolioId, pageSize: LOADED_RUNS })
  const navigate = useNavigate()
  const [search, setSearch] = useState('')
  const [status, setStatus] = useState('all')
  const [family, setFamily] = useState('all')
  const all = experiments.data?.items
  const rows = useMemo(() => (all ?? []).filter((e) =>
    (status === 'all' || e.status === status) &&
    (family === 'all' || e.modelFamily === family) &&
    (search.trim() === '' || contains(`${e.name} ${familyLabels[e.modelFamily] ?? ''} ${targetLabels[e.target] ?? ''} ${dateTime(e.createdAtUtc)}`, search)),
  ), [all, status, family, search])
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
    {
      key: 'delete', header: <span className="sr-only">Delete</span>, align: 'right',
      // A queued or running ML Optimization cannot be deleted (the server refuses it, too).
      cell: (e) => archived || e.status === 'Queued' || e.status === 'Running' ? null : <DeleteButton label={`Delete ${e.name}`} onClick={() => setToDelete(e)} />,
    },
  ]
  return (
    <>
      <h3 className="mt-6 mb-2 text-sm font-semibold">ML Optimizations</h3>
      <Card className="py-4">
        <CardContent className="space-y-3 px-4">
          <div className="flex flex-wrap gap-2">
            <SearchBox value={search} onChange={setSearch} label="Search ML Optimizations" />
            <Filter value={status} onChange={setStatus} options={mlStatusOptions} label="ML status filter" />
            <Filter value={family} onChange={setFamily} options={familyOptions} label="Model filter" width="w-52" />
          </div>
          {experiments.error ? <ErrorState error={experiments.error} /> : experiments.isLoading ? <LoadingBlock /> : (
            <>
              <DataTable
                rows={rows}
                columns={columns}
                rowKey={(e) => e.id}
                maxRows={VISIBLE_ROWS}
                onRowClick={(e) => navigate(portfolioPath(portfolioId, `ml-optimize/${e.id}`))}
                caption="ML Optimizations of this portfolio"
                empty={all?.length
                  ? <EmptyState title="No matching ML Optimizations" description="Change the search or the filters." />
                  : <EmptyState title="No ML Optimizations yet" description="Train one under ML Optimization; it appears here with its out-of-sample test metrics." />}
              />
              <ListCount shown={rows.length} loaded={all?.length ?? 0} total={experiments.data?.totalCount ?? 0} />
            </>
          )}
        </CardContent>
      </Card>
      <ConfirmDelete
        open={!!toDelete}
        title={toDelete ? `Delete “${toDelete.name}”?` : ''}
        description="The ML Optimization, its trained models and predictions are removed. Backtests and strategy comparisons keep their own copy of its settings. Holdings and transactions do not change. It cannot be undone."
        pending={remove.isPending}
        onClose={() => setToDelete(null)}
        onConfirm={async () => { if (toDelete) await remove.mutateAsync(toDelete.id) }}
      />
    </>
  )
}

/** The bin in a row; the click must not open the row. */
function DeleteButton({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <Button size="icon-xs" variant="ghost" aria-label={label} title="Delete" className="text-muted-foreground hover:text-destructive"
      onClick={(e) => { e.stopPropagation(); onClick() }} onKeyDown={(e) => e.stopPropagation()}>
      <Trash2 />
    </Button>
  )
}

function ConfirmDelete({ open, title, description, pending, onClose, onConfirm }: {
  open: boolean; title: string; description: string; pending: boolean; onClose: () => void; onConfirm: () => Promise<void>
}) {
  async function confirm() {
    try {
      await onConfirm()
      toast.success('Optimization deleted')
    } catch (e) {
      toast.error('Delete failed', { description: errorMessage(e) })
    }
    onClose()
  }
  return (
    <AlertDialog open={open} onOpenChange={(o) => !o && onClose()}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>{title}</AlertDialogTitle>
          <AlertDialogDescription>{description}</AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={() => void confirm()} disabled={pending}>Delete</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}

function SearchBox({ value, onChange, label }: { value: string; onChange: (v: string) => void; label: string }) {
  return (
    <div className="relative w-full sm:w-64">
      <Input type="search" placeholder="Search" aria-label={label} className="bg-card" value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  )
}

function Filter({ value, onChange, options, label, width = 'w-44' }: {
  value: string; onChange: (v: string) => void; options: { value: string; label: string }[]; label: string; width?: string
}) {
  return (
    <Select value={value} onValueChange={(v) => onChange(String(v))} items={options}>
      <SelectTrigger className={width} aria-label={label}><SelectValue /></SelectTrigger>
      <SelectContent>{options.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
    </Select>
  )
}

/** How many runs the list shows, and – when a portfolio has more than were loaded – that only the latest are searched. */
function ListCount({ shown, loaded, total }: { shown: number; loaded: number; total: number }) {
  if (loaded === 0) return null
  return (
    <p className="text-xs text-muted-foreground tabular">
      {shown === loaded ? `${loaded} run${loaded === 1 ? '' : 's'}` : `${shown} of ${loaded} runs match`}
      {loaded > VISIBLE_ROWS && ' · scroll the list for older runs'}
      {total > loaded && ` · the latest ${loaded} of ${total} are listed`}
    </p>
  )
}
