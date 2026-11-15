import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { DataTable, Pager } from '@/components/data-table'
import { EmptyState, ErrorState, InfoTip, LoadingBlock, Signed } from '@/components/common'
import { ProposalStatusBadge, TradeProposalView } from '@/components/proposal'
import { errorMessage } from '@/lib/api'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useCashLedger, useOrders, usePortfolioActivity, useProposals, useRebalancePreview, useReverseTransaction } from '@/lib/queries'
import type { ActivitySource, PortfolioActivity, TradeProposal } from '@/lib/types'
import { date, dateTime, money, qty } from '@/lib/format'
import { NoPortfolio } from './shared'

// Ledger (D-66): three tabs. Transactions lists everything that added to or took from the portfolio – every buy and sell,
// whether booked by hand, from a confirmed proposal or by Matrix Pilot, and every deposit, withdrawal, dividend and
// standalone fee – once, with where it came from. Proposals are what could be traded (with each executed proposal's
// order outcome); Cash is the full cash ledger. Orders, executions and recorded entries are no longer separate lists.

export function TradesPage() {
  const { portfolioId, portfolio, isLoading } = usePortfolioContext()
  const ccy = portfolio?.baseCurrency ?? 'USD'
  const rebalance = useRebalancePreview(portfolioId ?? '')
  const [tab, setTab] = useState('transactions')
  const archived = portfolio?.status === 'Archived'

  if (!isLoading && !portfolioId) return <NoPortfolio />

  async function onRebalance() {
    try {
      await rebalance.mutateAsync({})
      toast.success('Rebalance proposal created', { description: 'Review it below before confirming.' })
      setTab('proposals')
    } catch (e) {
      toast.error('Could not create rebalance proposal', { description: errorMessage(e) })
    }
  }

  return (
    <>
      <div className="mb-4 flex flex-wrap items-start justify-between gap-2">
        <div>
          <h2 className="sr-only">Transactions</h2>
          <p className="text-sm text-muted-foreground">
            What went into and out of this portfolio, what could be traded next, and its cash. Simulated paper trading; nothing is ever edited – corrections are new entries.
          </p>
        </div>
        {!archived && <Button variant="outline" onClick={onRebalance} disabled={rebalance.isPending}>Rebalance to latest run</Button>}
      </div>
      <Tabs value={tab} onValueChange={(v) => setTab(String(v))}>
        <TabsList className="nav-scroll max-w-full overflow-x-auto">
          <TabsTrigger value="transactions">Transactions</TabsTrigger>
          <TabsTrigger value="proposals">Proposals</TabsTrigger>
          <TabsTrigger value="cash">Cash</TabsTrigger>
        </TabsList>
        <TabsContent value="transactions" className="mt-4"><Activity currency={ccy} readOnly={archived} /></TabsContent>
        <TabsContent value="proposals" className="mt-4"><Proposals currency={ccy} /></TabsContent>
        <TabsContent value="cash" className="mt-4"><Cash currency={ccy} /></TabsContent>
      </Tabs>
    </>
  )
}

const sourceLabels: Record<ActivitySource, string> = {
  BookedByHand: 'By hand',
  TradeProposal: 'Trade proposal',
  MatrixPilot: 'Matrix Pilot',
  StartingCash: 'Starting cash',
  System: 'Platform',
}

const sourceHints: Record<ActivitySource, string> = {
  BookedByHand: 'Booked with “Record transaction”.',
  TradeProposal: 'A confirmed trade proposal from an optimization or a rebalance.',
  MatrixPilot: 'Booked by Matrix Pilot after its decision passed validation.',
  StartingCash: 'The cash the portfolio was created with.',
  System: 'Booked by the platform.',
}

const kindStyle: Partial<Record<PortfolioActivity['kind'], string>> = {
  Buy: 'text-gain',
  Sell: 'text-loss',
}

/** Everything that added to or took from the portfolio, newest first – each change once, with where it came from. */
function Activity({ currency, readOnly }: { currency: string; readOnly: boolean }) {
  const { portfolioId } = usePortfolioContext()
  const [page, setPage] = useState(1)
  const activity = usePortfolioActivity(portfolioId, page)
  const reverse = useReverseTransaction(portfolioId ?? '')
  const [pending, setPending] = useState<string | null>(null)

  async function onReverse(a: PortfolioActivity) {
    if (!a.journalId) return
    setPending(a.id)
    try {
      // One key per click on this row: a repeated click replays instead of reversing twice.
      await reverse.mutateAsync({ transactionId: a.journalId, idempotencyKey: `reverse-${a.journalId}` })
      toast.success('Reversal recorded', { description: 'The original stays in the list; an opposite entry was added.' })
    } catch (e) {
      toast.error('Could not reverse', { description: errorMessage(e) })
    }
    setPending(null)
  }

  if (activity.error) return <ErrorState error={activity.error} />
  if (!activity.data) return <LoadingBlock />
  return (
    <TabCard>
      <DataTable
        caption="Transactions"
        rows={activity.data.items}
        rowKey={(a) => a.id}
        empty={<EmptyState title="No transactions yet" description="Buys and sells (by hand, from a confirmed proposal or by Matrix Pilot), deposits, withdrawals, dividends and fees appear here." />}
        columns={[
          { key: 'date', header: 'Date', cell: (a) => <span className="text-xs tabular" title={dateTime(a.atUtc)}>{date(a.effectiveDate)}</span> },
          {
            key: 'what', header: 'What', cell: (a) => (
              <span className="flex flex-wrap items-center gap-1">
                <Badge variant="outline" className={kindStyle[a.kind]}>{a.kind}</Badge>
                {a.symbol && <span className="font-medium">{a.symbol}</span>}
                {a.reversed && <Badge variant="secondary">Reversed</Badge>}
              </span>
            ),
          },
          { key: 'qty', header: 'Shares', align: 'right', cell: (a) => (a.quantity == null ? '' : qty(a.quantity)) },
          {
            key: 'price', header: 'Price', align: 'right', cell: (a) => (a.price == null ? '' : (
              <span title={a.fxRate !== 1 ? `Exchanged at ${a.fxRate} ${currency} per ${a.currency}` : undefined}>{money(a.price, a.currency)}</span>
            )),
          },
          { key: 'amount', header: 'Cash effect', align: 'right', cell: (a) => <Signed value={a.amount}>{money(a.amount, currency, { signed: true })}</Signed> },
          { key: 'fees', header: 'Fees', align: 'right', cell: (a) => (a.fees ? money(a.fees, currency) : ''), className: 'hidden md:table-cell' },
          {
            key: 'pnl', header: 'Realized P&L', align: 'right', className: 'hidden md:table-cell',
            cell: (a) => (a.realizedPnl == null ? '' : <Signed value={a.realizedPnl}>{money(a.realizedPnl, currency, { signed: true })}</Signed>),
          },
          {
            key: 'source', header: 'From', cell: (a) => (
              <span className="text-xs text-muted-foreground">
                <InfoTip of={sourceLabels[a.source]}>{sourceHints[a.source]}{a.note ? ` ${a.note}` : ''}</InfoTip>
              </span>
            ),
          },
          {
            key: 'actions', header: '', cell: (a) => !readOnly && a.canReverse ? (
              <Button size="xs" variant="ghost" disabled={pending === a.id} onClick={() => void onReverse(a)}
                aria-label={`Reverse the ${a.kind.toLowerCase()} of ${date(a.effectiveDate)}`}>Reverse</Button>
            ) : null,
          },
        ]}
      />
      <Pager page={page} totalPages={activity.data.totalPages} onChange={setPage} />
      <p className="text-xs text-muted-foreground">
        Cash effect includes fees (in {currency}). A trade is corrected by recording the opposite trade; a deposit, withdrawal, dividend or fee booked by hand can be reversed.
      </p>
    </TabCard>
  )
}

function Proposals({ currency }: { currency: string }) {
  const { portfolioId } = usePortfolioContext()
  const { data, isLoading, error } = useProposals(portfolioId)
  const [open, setOpen] = useState<string | null>(null)
  if (isLoading) return <LoadingBlock />
  if (error) return <ErrorState error={error} />
  if (!data || data.items.length === 0)
    return <TabCard><EmptyState title="No trade proposals" description="Run an optimization and create a trade proposal, or rebalance to the latest run." /></TabCard>
  const expanded = open ?? data.items.find((p) => p.status === 'Pending')?.id ?? null
  return (
    <div className="space-y-2">
      {data.items.map((p) => {
        const isOpen = expanded === p.id
        const trades = p.lines.filter((l) => l.action === 'Buy' || l.action === 'Sell').length
        return (
          <Card key={p.id} className="gap-0 py-0">
            <button
              type="button"
              className="flex w-full items-center gap-3 px-4 py-3 text-left text-sm hover:bg-muted/50"
              aria-expanded={isOpen}
              onClick={() => setOpen(isOpen ? '' : p.id)}
            >
              {isOpen ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
              <ProposalStatusBadge status={p.status} />
              <span className="font-medium">{p.optimizationRunId ? 'From optimization run' : 'Manual targets'}</span>
              <span className="text-muted-foreground">{trades} trade{trades === 1 ? '' : 's'} · costs {money(p.estimatedCosts, currency)}</span>
              <span className="ml-auto text-xs text-muted-foreground">{dateTime(p.createdAtUtc)}</span>
            </button>
            {isOpen && (
              <CardContent className="space-y-3 border-t py-4">
                {p.status === 'Executed' && <OrderOutcome proposal={p} />}
                <TradeProposalView proposal={p} currency={currency} />
              </CardContent>
            )}
          </Card>
        )
      })}
    </div>
  )
}

/** How an executed proposal's orders turned out: the filled ones are in Transactions; a rejected one changed nothing. */
function OrderOutcome({ proposal }: { proposal: TradeProposal }) {
  const { portfolioId } = usePortfolioContext()
  const orders = useOrders(portfolioId, 1, proposal.id)
  const items = orders.data?.items ?? []
  if (items.length === 0) return null
  const rejected = items.filter((o) => o.status === 'Rejected')
  const partial = items.filter((o) => o.status === 'PartiallyFilled')
  const filled = items.length - rejected.length
  return (
    <div className="rounded-md border px-3 py-2 text-sm" role="status" aria-label="Order outcome">
      <span className="font-medium">{filled} of {items.length} order{items.length === 1 ? '' : 's'} filled</span>
      <span className="text-muted-foreground"> – the trades are listed under Transactions.</span>
      {partial.map((o) => <div key={o.id} className="text-xs text-muted-foreground">{o.side} {o.symbol}: {qty(o.filledQuantity)} of {qty(o.requestedQuantity)} filled{o.message ? ` – ${o.message}` : ''}</div>)}
      {rejected.map((o) => <div key={o.id} className="text-xs text-warn">{o.side} {o.symbol} rejected{o.message ? `: ${o.message}` : ''} – nothing was traded.</div>)}
    </div>
  )
}

function Cash({ currency }: { currency: string }) {
  const { portfolioId } = usePortfolioContext()
  const [page, setPage] = useState(1)
  const { data, isLoading, error } = useCashLedger(portfolioId, page)
  if (isLoading) return <LoadingBlock />
  if (error) return <ErrorState error={error} />
  return (
    <TabCard>
      <DataTable
        caption="Cash ledger"
        rows={data?.items ?? []}
        rowKey={(c) => c.id}
        columns={[
          { key: 't', header: 'Time', cell: (c) => dateTime(c.timestampUtc) },
          { key: 'ty', header: 'Type', cell: (c) => c.type.replace(/([a-z])([A-Z])/g, '$1 $2') },
          { key: 'd', header: 'Description', cell: (c) => <span className="text-muted-foreground">{c.description}</span> },
          { key: 'a', header: 'Amount', align: 'right', cell: (c) => <Signed value={c.amount}>{money(c.amount, currency, { signed: true })}</Signed> },
          { key: 'b', header: 'Balance after', align: 'right', cell: (c) => money(c.balanceAfter, currency) },
        ]}
      />
      {data && <Pager page={page} totalPages={data.totalPages} onChange={setPage} />}
    </TabCard>
  )
}

/** The surface of every sub-tab: the same translucent card as the key figures elsewhere. */
function TabCard({ children }: { children: ReactNode }): ReactNode {
  return <Card className="py-4"><CardContent className="space-y-3 px-4">{children}</CardContent></Card>
}
