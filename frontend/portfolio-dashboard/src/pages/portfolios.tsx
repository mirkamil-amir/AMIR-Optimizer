import { useState } from 'react'
import { Link } from 'react-router'
import { Archive, ArchiveRestore, BarChart3, Briefcase, ListChecks, Plus, ReceiptText, Sigma, Trash2 } from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { CurrencyConverterCard } from '@/components/currency-converter'
import { DataBadge, EmptyState, ErrorState, InfoTip, LoadingBlock, MetricCard, PageHeader, Signed } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { date, money, relativeTime } from '@/lib/format'
import { portfolioPath } from '@/lib/portfolio-context'
import { useArchivePortfolio, useDeletePortfolio, useFxInfo, usePortfolioOverview } from '@/lib/queries'
import type { PortfolioSummary } from '@/lib/types'

/**
 * Overview: every portfolio of the signed-in user (the server returns only the caller's), with a total over the active
 * ones. Values come from stored data; a portfolio that cannot be valued says so instead of showing a number.
 */
export function PortfoliosPage() {
  const [currency, setCurrency] = useState<string>()
  const overview = usePortfolioOverview(currency)
  const fx = useFxInfo()
  const data = overview.data
  const ccy = data?.currency ?? currency ?? 'USD'
  const active = data?.portfolios.filter((p) => p.status === 'Active') ?? []
  const archived = data?.portfolios.filter((p) => p.status === 'Archived') ?? []
  const currencies = fx.data?.currencies ?? [ccy]
  const profit = data?.totalValue != null && data.totalNetContributions != null ? data.totalValue - data.totalNetContributions : null

  return (
    <>
      <PageHeader
        title="My portfolios"
        description="Each portfolio is independent: its own cash, holdings, transactions, optimization runs and history. Simulated – no real orders."
        actions={<Button render={<Link to="/stocks" />}><Plus /> Create portfolio</Button>}
      />

      {overview.error ? <ErrorState error={overview.error} onRetry={() => overview.refetch()} /> : !data ? <LoadingBlock height="h-72" /> : data.portfolios.length === 0 ? (
        <EmptyState
          icon={<Briefcase className="size-8" />}
          title="No portfolios yet"
          description="Create your first portfolio: give it a name, a currency and simulated starting cash, pick the stocks it may invest in, then run an optimization or record transactions."
          action={<Button render={<Link to="/stocks" />}><Plus /> Create your first portfolio</Button>}
        />
      ) : (
        <>
          <section aria-label="Totals across active portfolios" className="mb-6 space-y-2">
            <div className="flex flex-wrap items-center justify-between gap-2">
              <h2 className="flex items-center gap-1.5 text-sm font-medium">
                Across {data.activePortfolios} active portfolio{data.activePortfolios === 1 ? '' : 's'}
                <InfoTip label="How totals are calculated">{data.methodology}</InfoTip>
              </h2>
              <Select value={ccy} onValueChange={(v) => v && setCurrency(String(v))} items={currencies.map((c) => ({ value: c, label: c }))}>
                <SelectTrigger className="w-28" aria-label="Currency of the totals"><SelectValue /></SelectTrigger>
                <SelectContent>{currencies.map((c) => <SelectItem key={c} value={c}>{c}</SelectItem>)}</SelectContent>
              </Select>
            </div>
            <div className="grid grid-cols-2 gap-3 md:grid-cols-3">
              <MetricCard label={`Total value (${ccy})`} value={data.totalValue == null ? 'Not available' : money(data.totalValue, ccy)}
                sub={data.totalValue == null ? 'See the note below' : 'Converted at today’s reference rates'}
                hint="Sum of the active portfolios' total values (cash + positions), each converted into this currency at today's reference rate." />
              <MetricCard label="Net contributions" value={data.totalNetContributions == null ? '—' : money(data.totalNetContributions, ccy)}
                sub="Deposits − withdrawals" hint="Money paid into the portfolios minus money taken out. Not a gain." />
              <MetricCard label="Profit / loss" tone="signed" numeric={profit} value={profit == null ? '—' : money(profit, ccy, { signed: true })}
                sub="Total value − net contributions" hint="Includes realized and unrealized P&L, fees and recorded dividends. Not a return figure: it ignores the timing of cash flows." />
            </div>
            {data.totalProblems.length > 0 && (
              <ul role="status" className="space-y-0.5 text-xs text-warn">{data.totalProblems.map((p) => <li key={p}>{p}</li>)}</ul>
            )}
          </section>

          <section aria-label="Active portfolios">
            {active.length === 0 ? (
              <EmptyState title="No active portfolio" description="All your portfolios are archived. Restore one below or create a new one."
                action={<Button render={<Link to="/stocks" />}><Plus /> Create portfolio</Button>} />
            ) : (
              <ul className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                {active.map((p) => <li key={p.id}><PortfolioCard p={p} /></li>)}
              </ul>
            )}
          </section>

          {archived.length > 0 && <ArchivedList items={archived} />}
        </>
      )}
      {data && <CurrencyConverterCard base={ccy} className="mt-8" />}
    </>
  )
}

function PortfolioCard({ p }: { p: PortfolioSummary }) {
  const ccy = p.baseCurrency
  return (
    <Card className="h-full" aria-label={`Portfolio ${p.name}`}>
      <CardHeader className="gap-1">
        <div className="flex items-start justify-between gap-2">
          <CardTitle className="text-base">
            <Link to={portfolioPath(p.id)} className="hover:underline focus-visible:underline">{p.name}</Link>
          </CardTitle>
          <div className="flex shrink-0 items-center gap-1">
            <Badge variant="outline" className="tabular">{ccy}</Badge>
            {p.usesSimulatedData && <DataBadge kind="simulated" />}
          </div>
        </div>
        {p.description && <CardDescription className="line-clamp-2">{p.description}</CardDescription>}
      </CardHeader>
      <CardContent className="space-y-3">
        <div>
          <div className="text-xs text-muted-foreground">Total value</div>
          <div className="text-xl font-semibold tabular">{p.totalValue === null ? 'Not available' : money(p.totalValue, ccy)}</div>
          {p.totalValue === null && <p className="text-xs text-warn">{p.valuationProblems?.join(' ') || 'A position cannot be valued.'}</p>}
        </div>
        <dl className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-sm">
          <Row label="Cash" value={money(p.cashBalance, ccy)} />
          <Row label="Invested" value={p.marketValue === null ? '—' : money(p.marketValue, ccy)} />
          <Row label="Holdings" value={String(p.holdings)} />
          <div>
            <dt className="text-xs text-muted-foreground">Profit / loss</dt>
            <dd className="tabular font-medium" title="Total value − net contributions (deposits − withdrawals)">
              {p.profitLoss === null ? '—' : <Signed value={p.profitLoss}>{money(p.profitLoss, ccy, { signed: true })}</Signed>}
            </dd>
          </div>
          <Row label="Last optimization" value={p.latestOptimizationAtUtc ? relativeTime(p.latestOptimizationAtUtc) : 'none yet'} />
          <Row label="Last activity" value={relativeTime(p.lastActivityAtUtc)} />
        </dl>
        <p className="text-[11px] text-muted-foreground">
          {p.valuationDate ? `Valued at stored prices of ${date(p.valuationDate)}` : 'Cash only – no positions to value'}
          {p.stalePositions > 0 && <span className="text-warn"> · {p.stalePositions} stale price{p.stalePositions === 1 ? '' : 's'}</span>}
        </p>
      </CardContent>
      <CardFooter className="flex flex-wrap gap-1.5">
        <Button size="sm" render={<Link to={portfolioPath(p.id)} />} aria-label={`Open ${p.name}`}>Open</Button>
        <Button size="sm" variant="outline" render={<Link to={portfolioPath(p.id, 'optimize')} />} aria-label={`Optimize ${p.name}`}><Sigma /> Optimize</Button>
        <Button size="sm" variant="ghost" render={<Link to={portfolioPath(p.id, 'holdings')} />} aria-label={`Holdings of ${p.name}`}><Briefcase /> Holdings</Button>
        <Button size="sm" variant="ghost" render={<Link to={portfolioPath(p.id, 'transactions')} />} aria-label={`Transactions of ${p.name}`}><ReceiptText /> Transactions</Button>
        <Button size="sm" variant="ghost" render={<Link to={portfolioPath(p.id, 'performance')} />} aria-label={`Performance of ${p.name}`}><BarChart3 /> Performance</Button>
        <Button size="sm" variant="ghost" render={<Link to={`/history?portfolio=${p.id}`} />} aria-label={`Optimization runs of ${p.name}`}><ListChecks /> Runs</Button>
      </CardFooter>
    </Card>
  )
}

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{label}</dt>
      <dd className="tabular font-medium">{value}</dd>
    </div>
  )
}

function ArchivedList({ items }: { items: PortfolioSummary[] }) {
  const archive = useArchivePortfolio()
  const remove = useDeletePortfolio()
  const [toDelete, setToDelete] = useState<PortfolioSummary | null>(null)

  async function restore(p: PortfolioSummary) {
    try {
      await archive.mutateAsync({ id: p.id, archive: false })
      toast.success(`“${p.name}” restored`)
    } catch (e) {
      toast.error('Could not restore', { description: errorMessage(e) })
    }
  }
  async function confirmDelete() {
    if (!toDelete) return
    try {
      await remove.mutateAsync(toDelete.id)
      toast.success(`Deleted “${toDelete.name}”`)
    } catch (e) {
      toast.error('Delete failed', { description: errorMessage(e) })
    }
    setToDelete(null)
  }

  return (
    <section aria-label="Archived portfolios" className="mt-8">
      <h2 className="mb-2 flex items-center gap-1.5 text-sm font-medium"><Archive className="size-4" /> Archived ({items.length})</h2>
      <ul className="divide-y rounded-lg border bg-background">
        {items.map((p) => (
          <li key={p.id} className="flex flex-wrap items-center justify-between gap-2 px-3 py-2 text-sm">
            <div className="min-w-0">
              <Link to={portfolioPath(p.id)} className="font-medium hover:underline">{p.name}</Link>
              <div className="text-xs text-muted-foreground">
                {p.baseCurrency} · {p.holdings} holding{p.holdings === 1 ? '' : 's'} · archived {relativeTime(p.archivedAtUtc)} · history kept, read-only
              </div>
            </div>
            <div className="flex gap-1.5">
              <Button size="sm" variant="outline" onClick={() => void restore(p)} aria-label={`Restore ${p.name}`}><ArchiveRestore /> Restore</Button>
              <Button size="sm" variant="ghost" onClick={() => setToDelete(p)} aria-label={`Delete ${p.name} permanently`}><Trash2 /> Delete</Button>
            </div>
          </li>
        ))}
      </ul>
      <AlertDialog open={!!toDelete} onOpenChange={(o) => !o && setToDelete(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete “{toDelete?.name}” permanently?</AlertDialogTitle>
            <AlertDialogDescription>
              This removes the portfolio with its holdings, transactions, cash ledger, proposals and optimization runs. It cannot be undone.
              Research sessions that refer to it are kept.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction variant="destructive" onClick={() => void confirmDelete()}>Delete permanently</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  )
}
