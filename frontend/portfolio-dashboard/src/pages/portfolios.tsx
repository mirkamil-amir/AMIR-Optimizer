import { useState } from 'react'
import { Link } from 'react-router'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardFooter, CardHeader, CardTitle } from '@/components/ui/card'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { CurrencyConverterCard } from '@/components/currency-converter'
import { DataBadge, EmptyState, ErrorState, InfoTip, LoadingBlock, MetricCard, PageHeader, Signed } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { date, money, relativeTime } from '@/lib/format'
import { portfolioPath } from '@/lib/portfolio-context'
import { useArchivePortfolio, useDeletePortfolio, useFxInfo, usePortfolioOverview, useRefreshPortfolioQuotes } from '@/lib/queries'
import type { PortfolioSummary } from '@/lib/types'
import { CurrencySelect } from '@/components/currency'
import { venues } from '@/lib/markets'
import { useMapHighlight } from '@/lib/world-map'

const allCountries = venues.map((v) => v.country)

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
  // Every exchange city lights up on the background map while the portfolios are listed.
  useMapHighlight('portfolios', allCountries)
  const profit = data?.totalValue != null && data.totalNetContributions != null ? data.totalValue - data.totalNetContributions : null

  return (
    <>
      <PageHeader
        title="My portfolios"
        info="Every portfolio of your account. Each one is private and independent: its own currency, cash, holdings and optimizations."
        actions={<Button render={<Link to="/stocks" />}>Forge a portfolio</Button>}
      />

      {overview.error ? <ErrorState error={overview.error} onRetry={() => overview.refetch()} /> : !data ? <LoadingBlock height="h-72" /> : data.portfolios.length === 0 ? (
        <EmptyState
          title="No portfolios yet"
          description="Create your first portfolio: give it a name, a currency and simulated starting cash, pick the stocks it may invest in, then run an optimization or record transactions."
          action={<Button render={<Link to="/stocks" />}>Forge your first portfolio</Button>}
        />
      ) : (
        <>
          <section aria-label="Totals across active portfolios" className="mb-6 space-y-2">
            {/* The currency belongs to the totals, so it sits in their heading line, not beside the page's actions. */}
            <div className="flex flex-wrap items-center gap-2">
              <h2 className="flex items-center gap-1.5 text-sm font-medium">
                <InfoTip of={`Across ${data.activePortfolios} active portfolio${data.activePortfolios === 1 ? '' : 's'}`}>{data.methodology}</InfoTip>
              </h2>
              <span aria-hidden className="text-sm text-muted-foreground">in</span>
              <CurrencySelect value={ccy} onChange={setCurrency} supported={fx.data?.currencies} label="Currency of the totals" className="w-28" />
            </div>
            <div className="grid grid-cols-2 gap-3 md:grid-cols-3">
              <MetricCard label={`Total value (${ccy})`} value={data.totalValue == null ? 'Not available' : money(data.totalValue, ccy)}
                sub={data.totalValue == null ? 'See the note below' : undefined}
                hint="Sum of the active portfolios' total values (cash + positions), each converted into this currency at today's reference rate." />
              <MetricCard label="Net contributions" value={data.totalNetContributions == null ? '—' : money(data.totalNetContributions, ccy)}
                hint="Money paid into the portfolios minus money taken out, each deposit and withdrawal converted at the rate of its own date. Not a gain." />
              <MetricCard label="Profit / loss" tone="signed" numeric={profit} value={profit == null ? '—' : money(profit, ccy, { signed: true })}
                hint="Includes realized and unrealized P&L, fees, recorded dividends and – in another currency – the currency effect since the money came in. Not a return figure: it ignores the timing of cash flows." />
            </div>
            {data.totalProblems.length > 0 && (
              <ul role="status" className="space-y-0.5 text-xs text-warn">{data.totalProblems.map((p) => <li key={p}>{p}</li>)}</ul>
            )}
          </section>

          <section aria-label="Active portfolios">
            {active.length === 0 ? (
              <EmptyState title="No active portfolio" description="All your portfolios are archived. Restore one below or create a new one."
                action={<Button render={<Link to="/stocks" />}>Forge a portfolio</Button>} />
            ) : (
              // Equal rows: every card has the height of the tallest one, with or without a description.
              <ul className="grid auto-rows-fr gap-4 md:grid-cols-2 xl:grid-cols-3">
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
  // Amounts in the currency chosen at the top (converted by the server), or in the portfolio's own currency.
  const view = p.inCurrency
  const converted = !!view && view.currency !== p.baseCurrency
  const ccy = view?.currency ?? p.baseCurrency
  const total = view ? view.totalValue : p.totalValue
  const profitLoss = view ? view.profitLoss : p.profitLoss
  const refresh = useRefreshPortfolioQuotes()
  const [deleting, setDeleting] = useState(false)

  async function refreshPrices() {
    try {
      const result = await refresh.mutateAsync(p.id)
      const updated = result.items.filter((i) => i.outcome === 'Updated').length
      const fresh = result.items.filter((i) => i.outcome === 'Fresh').length
      const failed = result.items.filter((i) => i.outcome === 'Failed' || i.outcome === 'Unsupported')
      toast.success(`${p.name}: ${updated} updated, ${fresh} already recent${failed.length ? `, ${failed.length} unavailable` : ''}`, {
        description: failed.length ? failed.map((f) => `${f.symbol}: ${f.message ?? f.outcome}`).join(' · ') : undefined,
      })
    } catch (e) {
      toast.error('Refresh failed', { description: errorMessage(e) })
    }
  }

  return (
    <Card className="h-full" aria-label={`Portfolio ${p.name}`}>
      <CardHeader className="gap-1">
        <div className="flex items-start justify-between gap-2">
          <CardTitle className="text-base">
            <Link to={portfolioPath(p.id)} className="hover:underline focus-visible:underline">{p.name}</Link>
          </CardTitle>
          <div className="flex shrink-0 items-center gap-1">
            <Badge variant="outline" className="tabular" title="The portfolio's own currency">{p.baseCurrency}</Badge>
            {p.usesSimulatedData && <DataBadge kind="simulated" />}
          </div>
        </div>
        {/* Two lines are always reserved, so cards with and without a description line up. */}
        <CardDescription className="line-clamp-2 min-h-10">{p.description}</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div>
          <div className="text-xs text-muted-foreground">Total value{converted ? ` in ${ccy}` : ''}</div>
          <div className="text-xl font-semibold tabular">{total === null ? 'Not available' : money(total, ccy)}</div>
          {converted && p.totalValue !== null && <div className="text-xs text-muted-foreground tabular">{money(p.totalValue, p.baseCurrency)} in its own currency</div>}
          {total === null && <p className="text-xs text-warn">{(converted ? view!.problems : p.valuationProblems)?.join(' ') || 'A position cannot be valued.'}</p>}
        </div>
        <dl className="grid grid-cols-2 gap-x-4 gap-y-1.5 text-sm">
          <Row label="Cash" value={(view ? view.cash : p.cashBalance) === null ? '—' : money(view ? view.cash : p.cashBalance, ccy)} />
          <Row label="Invested" value={(view ? view.marketValue : p.marketValue) === null ? '—' : money(view ? view.marketValue : p.marketValue, ccy)} />
          <Row label="Holdings" value={String(p.holdings)} />
          <div>
            <dt className="text-xs text-muted-foreground">Profit / loss</dt>
            <dd className="tabular font-medium" title={converted
              ? `Total value − net contributions in ${ccy}; each deposit and withdrawal at the rate of its date, so this includes the currency effect`
              : 'Total value − net contributions (deposits − withdrawals)'}>
              {profitLoss === null ? '—' : <Signed value={profitLoss}>{money(profitLoss, ccy, { signed: true })}</Signed>}
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
      <CardFooter className="mt-auto flex gap-1.5">
        <Button size="sm" render={<Link to={portfolioPath(p.id)} />} aria-label={`Open ${p.name}`}>Open</Button>
        <Button size="sm" variant="outline" onClick={() => void refreshPrices()} disabled={refresh.isPending || p.holdings === 0}
          title={p.holdings === 0 ? 'No positions to refresh' : 'Refresh the latest quotes of the positions'} aria-label={`Refresh prices of ${p.name}`}>
          Refresh prices
        </Button>
        <Button size="sm" variant="ghost" className="ml-auto text-destructive hover:text-destructive" onClick={() => setDeleting(true)}
          aria-label={`Delete ${p.name}`}>Delete</Button>
      </CardFooter>
      <DeletePortfolioDialog portfolio={deleting ? p : null} onClose={() => setDeleting(false)} />
    </Card>
  )
}

/** Confirmation before a portfolio and its whole simulated history are deleted for good (active or archived). */
function DeletePortfolioDialog({ portfolio, onClose }: { portfolio: PortfolioSummary | null; onClose: () => void }) {
  const remove = useDeletePortfolio()

  async function confirmDelete() {
    if (!portfolio) return
    try {
      await remove.mutateAsync(portfolio.id)
      toast.success(`Deleted “${portfolio.name}”`)
    } catch (e) {
      toast.error('Delete failed', { description: errorMessage(e) })
    }
    onClose()
  }

  return (
    <AlertDialog open={!!portfolio} onOpenChange={(o) => !o && onClose()}>
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete “{portfolio?.name}” permanently?</AlertDialogTitle>
          <AlertDialogDescription>
            This removes the portfolio and every record of it: holdings, transactions, cash ledger, proposals, Math and ML Optimizations,
            backtests, strategy comparisons and research sessions. Stock prices are shared and stay. It cannot be undone.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction variant="destructive" onClick={() => void confirmDelete()} disabled={remove.isPending}>Delete permanently</AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
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
  const [toDelete, setToDelete] = useState<PortfolioSummary | null>(null)

  async function restore(p: PortfolioSummary) {
    try {
      await archive.mutateAsync({ id: p.id, archive: false })
      toast.success(`“${p.name}” restored`)
    } catch (e) {
      toast.error('Could not restore', { description: errorMessage(e) })
    }
  }

  return (
    <section aria-label="Archived portfolios" className="mt-8">
      <h2 className="mb-2 flex items-center gap-1.5 text-sm font-medium">Archived ({items.length})</h2>
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
              <Button size="sm" variant="outline" onClick={() => void restore(p)} aria-label={`Restore ${p.name}`}>Restore</Button>
              <Button size="sm" variant="ghost" onClick={() => setToDelete(p)} aria-label={`Delete ${p.name} permanently`}>Delete</Button>
            </div>
          </li>
        ))}
      </ul>
      <DeletePortfolioDialog portfolio={toDelete} onClose={() => setToDelete(null)} />
    </section>
  )
}
