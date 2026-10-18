import { Suspense, useEffect, useState } from 'react'
import { Link, Navigate, Outlet, useLocation, useNavigate, useParams } from 'react-router'
import { Archive, ArchiveRestore, ArrowLeft, FolderSearch, MoreHorizontal, Pencil, Plus, Sparkles } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuSeparator, DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { DataBadge, EmptyState, LoadingBlock } from '@/components/common'
import { TransactionDialog } from '@/components/transaction-form'
import { errorMessage } from '@/lib/api'
import { date, money, relativeTime } from '@/lib/format'
import { PortfolioScope, portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { useArchivePortfolio, usePortfolio, useRun, useUpdatePortfolioDetails } from '@/lib/queries'
import type { PortfolioSummary } from '@/lib/types'
import { cn } from '@/lib/utils'

/** Sections of one portfolio, in the order of the tab bar. `runs` leads to the optimization history filtered to it. */
const SECTIONS = [
  { path: '', label: 'Overview' },
  { path: 'optimize', label: 'Optimize' },
  { path: 'holdings', label: 'Holdings' },
  { path: 'transactions', label: 'Transactions' },
  { path: 'performance', label: 'Performance' },
] as const

/** The section of the current URL inside `/portfolios/:id/…` (run pages belong to Optimize). */
function sectionOf(pathname: string, id: string) {
  const rest = pathname.slice(portfolioPath(id).length).replace(/^\/+/, '')
  return rest.split('/')[0] ?? ''
}

/**
 * Route element of `/portfolios/:portfolioId/*`: resolves the portfolio from the URL (never from browser memory), shows
 * who/what is being worked on and renders the section below it. Sections are remounted when the portfolio changes, so no
 * form state, selection of runs or proposal of one portfolio can appear under another.
 */
export function PortfolioWorkspace() {
  const { portfolioId = '' } = useParams()
  const { portfolios, isLoading, select } = usePortfolioContext()
  const portfolio = portfolios.find((p) => p.id === portfolioId)

  useEffect(() => {
    if (portfolio && portfolio.status === 'Active') select(portfolio.id)
  }, [portfolio, select])

  if (isLoading) return <LoadingBlock height="h-96" />
  if (!portfolio) {
    // Unknown, deleted or another user's id: the same safe answer (the server returns 404 for all of them).
    return (
      <EmptyState
        icon={<FolderSearch className="size-8" />}
        title="Portfolio not found"
        description="It may have been deleted, or the link does not belong to your account."
        action={<Button render={<Link to="/" />}><ArrowLeft /> My portfolios</Button>}
      />
    )
  }
  return (
    <PortfolioScope portfolioId={portfolio.id}>
      <WorkspaceHeader portfolio={portfolio} />
      <div key={portfolio.id}>
        <Suspense fallback={<LoadingBlock height="h-96" />}>
          <Outlet />
        </Suspense>
      </div>
    </PortfolioScope>
  )
}

function WorkspaceHeader({ portfolio }: { portfolio: PortfolioSummary }) {
  const { portfolios } = usePortfolioContext()
  const { pathname } = useLocation()
  const navigate = useNavigate()
  const details = usePortfolio(portfolio.id)
  const archive = useArchivePortfolio()
  const [recording, setRecording] = useState(false)
  const [editing, setEditing] = useState(false)
  const [confirmArchive, setConfirmArchive] = useState(false)
  const section = sectionOf(pathname, portfolio.id)
  const archived = portfolio.status === 'Archived'
  const ccy = portfolio.baseCurrency
  const switchable = portfolios.filter((p) => p.status === 'Active' || p.id === portfolio.id)

  async function toggleArchive() {
    try {
      await archive.mutateAsync({ id: portfolio.id, archive: !archived })
      toast.success(archived ? `“${portfolio.name}” restored` : `“${portfolio.name}” archived`, {
        description: archived ? 'It accepts transactions and runs again.' : 'Its history stays available; it accepts no changes until restored.',
      })
    } catch (e) {
      toast.error('Could not change the portfolio', { description: errorMessage(e) })
    }
    setConfirmArchive(false)
  }

  return (
    <section aria-label="Current portfolio" className="mb-6 space-y-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="min-w-0 space-y-1">
          <Link to="/" className="inline-flex items-center gap-1 text-xs text-muted-foreground hover:text-foreground"><ArrowLeft className="size-3" /> My portfolios</Link>
          <div className="flex flex-wrap items-center gap-2">
            <h1 className="truncate text-2xl font-semibold tracking-tight">{portfolio.name}</h1>
            <Badge variant="outline" className="tabular">{ccy}</Badge>
            {archived && <Badge variant="secondary"><Archive className="size-3" /> Archived</Badge>}
            {portfolio.usesSimulatedData && <DataBadge kind="simulated" />}
          </div>
          {portfolio.description && <p className="text-sm text-muted-foreground">{portfolio.description}</p>}
        </div>

        <div className="flex flex-wrap items-center gap-2">
          {switchable.length > 1 && (
            <Select
              value={portfolio.id}
              onValueChange={(v) => {
                // Same section of the other portfolio; a run page goes to that portfolio's optimizer.
                if (v && v !== portfolio.id) navigate(portfolioPath(String(v), section || undefined))
              }}
              items={switchable.map((p) => ({ value: p.id, label: p.name }))}
            >
              <SelectTrigger className="w-52" aria-label="Switch portfolio"><SelectValue /></SelectTrigger>
              <SelectContent>
                {switchable.map((p) => <SelectItem key={p.id} value={p.id}>{p.name}</SelectItem>)}
              </SelectContent>
            </Select>
          )}
          {!archived && <Button onClick={() => setRecording(true)}><Plus /> Record transaction</Button>}
          <DropdownMenu>
            <DropdownMenuTrigger render={<Button variant="outline" size="icon" aria-label="More portfolio actions" />}><MoreHorizontal /></DropdownMenuTrigger>
            <DropdownMenuContent align="end" className="w-56">
              <DropdownMenuItem onClick={() => setEditing(true)}><Pencil /> Rename or describe</DropdownMenuItem>
              <DropdownMenuItem onClick={() => navigate(`/research?portfolio=${portfolio.id}`)}><Sparkles /> Research for this portfolio</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem onClick={() => (archived ? void toggleArchive() : setConfirmArchive(true))}>
                {archived ? <><ArchiveRestore /> Restore</> : <><Archive /> Archive</>}
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </div>

      <dl className="grid grid-cols-2 gap-x-6 gap-y-1 text-sm sm:flex sm:flex-wrap">
        <Fact label="Total value" value={portfolio.totalValue === null ? 'Not available' : money(portfolio.totalValue, ccy)} />
        <Fact label="Cash" value={money(portfolio.cashBalance, ccy)} />
        <Fact label="Holdings" value={String(portfolio.holdings)} />
        <Fact label="Valued at" value={portfolio.valuationDate ? `stored prices of ${date(portfolio.valuationDate)}` : portfolio.holdings === 0 ? 'cash only' : '—'} />
        <Fact label="Last activity" value={relativeTime(portfolio.lastActivityAtUtc)} />
      </dl>
      {(portfolio.valuationProblems?.length ?? 0) > 0 && (
        <p role="status" className="text-xs text-warn">Value unavailable: {portfolio.valuationProblems!.join(' ')}</p>
      )}
      {portfolio.stalePositions > 0 && (
        <p role="status" className="text-xs text-warn">{portfolio.stalePositions} position{portfolio.stalePositions === 1 ? ' has a' : 's have'} stale prices; values may be out of date.</p>
      )}
      {archived && (
        <p role="status" className="rounded-md border bg-muted/50 px-3 py-2 text-sm">
          This portfolio is archived: its history is read-only. Restore it to record transactions or run optimizations.
        </p>
      )}

      <nav aria-label="Portfolio sections" className="nav-scroll -mx-1 overflow-x-auto border-b">
        <ul className="flex min-w-max gap-0.5 px-1">
          {SECTIONS.map((s) => {
            const active = section === s.path
            return (
              <li key={s.path}>
                <Link
                  to={portfolioPath(portfolio.id, s.path || undefined)}
                  aria-current={active ? 'page' : undefined}
                  className={cn(
                    'inline-flex border-b-2 px-3 py-2 text-sm whitespace-nowrap transition-colors focus-visible:rounded-t-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none focus-visible:ring-inset',
                    active ? 'border-primary font-semibold text-foreground' : 'border-transparent text-muted-foreground hover:border-border hover:text-foreground',
                  )}
                >
                  {s.label}
                </Link>
              </li>
            )
          })}
          <li>
            <Link to={`/history?portfolio=${portfolio.id}`}
              className="inline-flex border-b-2 border-transparent px-3 py-2 text-sm whitespace-nowrap text-muted-foreground hover:border-border hover:text-foreground focus-visible:rounded-t-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none focus-visible:ring-inset">
              Optimization runs
            </Link>
          </li>
        </ul>
      </nav>

      <TransactionDialog
        open={recording}
        onOpenChange={setRecording}
        portfolioId={portfolio.id}
        portfolioName={portfolio.name}
        currency={ccy}
        symbols={details.data?.settings.universe ?? []}
      />
      {editing && <DetailsDialog portfolio={portfolio} onClose={() => setEditing(false)} />}
      <AlertDialog open={confirmArchive} onOpenChange={setConfirmArchive}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Archive “{portfolio.name}”?</AlertDialogTitle>
            <AlertDialogDescription>
              Its holdings, transactions and optimization runs stay available to read. While archived it accepts no transactions, runs or
              proposals, and pending proposals are cancelled. You can restore it at any time; only an archived portfolio can be deleted.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction onClick={() => void toggleArchive()}>Archive</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  )
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex gap-1.5">
      <dt className="text-muted-foreground">{label}</dt>
      <dd className="font-medium tabular">{value}</dd>
    </div>
  )
}

function DetailsDialog({ portfolio, onClose }: { portfolio: PortfolioSummary; onClose: () => void }) {
  const update = useUpdatePortfolioDetails(portfolio.id)
  const [name, setName] = useState(portfolio.name)
  const [description, setDescription] = useState(portfolio.description ?? '')
  const [error, setError] = useState<string | null>(null)
  async function save(e: React.FormEvent) {
    e.preventDefault()
    if (!name.trim()) return setError('Enter a name.')
    try {
      await update.mutateAsync({ name: name.trim(), description: description.trim() || null })
      toast.success('Portfolio updated')
      onClose()
    } catch (err) {
      setError(errorMessage(err))
    }
  }
  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent className="sm:max-w-md">
        <form onSubmit={save} className="space-y-4" noValidate>
          <DialogHeader>
            <DialogTitle>Rename or describe</DialogTitle>
            <DialogDescription>Changes only the name and description – not holdings, settings or history.</DialogDescription>
          </DialogHeader>
          <div className="space-y-1.5">
            <Label htmlFor="pf-name">Name</Label>
            <Input id="pf-name" maxLength={100} value={name} onChange={(e) => setName(e.target.value)} aria-invalid={!name.trim()} />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="pf-desc">Description (optional)</Label>
            <Input id="pf-desc" maxLength={500} value={description} onChange={(e) => setDescription(e.target.value)} />
          </div>
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>Cancel</Button>
            <Button type="submit" disabled={update.isPending}>Save</Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}

/**
 * Old portfolio-less URLs (`/optimize`, `/holdings`, `/trades`, `/analytics`) and bookmarks: open the same section of the
 * most recently opened portfolio, or the portfolio list when there is none.
 */
export function ToCurrentPortfolio({ section }: { section: string }) {
  const { portfolioId, isLoading } = usePortfolioContext()
  const { search } = useLocation()
  if (isLoading) return <LoadingBlock height="h-96" />
  return <Navigate to={portfolioId ? `${portfolioPath(portfolioId, section)}${search}` : '/'} replace />
}

/** `/optimize/:runId`: a run belongs to exactly one portfolio – open it inside that portfolio's workspace. */
export function RunRedirect() {
  const { runId = '' } = useParams()
  const run = useRun(runId)
  if (run.error) {
    return (
      <EmptyState icon={<FolderSearch className="size-8" />} title="Optimization run not found"
        description="It may have been deleted with its portfolio, or the link does not belong to your account."
        action={<Button render={<Link to="/history" />}>Optimization history</Button>} />
    )
  }
  if (!run.data) return <LoadingBlock height="h-96" />
  return <Navigate to={portfolioPath(run.data.portfolioId, `optimize/${run.data.id}`)} replace />
}
