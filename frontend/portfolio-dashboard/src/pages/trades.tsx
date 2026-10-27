import { useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { ChevronDown, ChevronRight } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { DataTable, Pager } from '@/components/data-table'
import { EmptyState, ErrorState, LoadingBlock, Signed } from '@/components/common'
import { FxNote } from '@/components/currency'
import { ProposalStatusBadge, TradeProposalView } from '@/components/proposal'
import { errorMessage } from '@/lib/api'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useCashLedger, useJournal, useOrders, useProposals, useRebalancePreview, useReverseTransaction, useTransactions } from '@/lib/queries'
import type { PortfolioTransaction } from '@/lib/types'
import { date, dateTime, money, qty } from '@/lib/format'
import { NoPortfolio } from './shared'

export function TradesPage() {
  const { portfolioId, portfolio, isLoading } = usePortfolioContext()
  const ccy = portfolio?.baseCurrency ?? 'USD'
  const rebalance = useRebalancePreview(portfolioId ?? '')
  const [tab, setTab] = useState('journal')
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
            Recorded transactions, trade proposals, simulated orders, executions and the cash ledger. Append-only: corrections are new entries.
          </p>
        </div>
        {!archived && <Button variant="outline" onClick={onRebalance} disabled={rebalance.isPending}>Rebalance to latest run</Button>}
      </div>
      <Tabs value={tab} onValueChange={(v) => setTab(String(v))}>
        <TabsList className="nav-scroll max-w-full overflow-x-auto">
          <TabsTrigger value="journal">Recorded</TabsTrigger>
          <TabsTrigger value="proposals">Proposals</TabsTrigger>
          <TabsTrigger value="transactions">Executions</TabsTrigger>
          <TabsTrigger value="orders">Orders</TabsTrigger>
          <TabsTrigger value="cash">Cash</TabsTrigger>
        </TabsList>
        <TabsContent value="journal" className="mt-4"><Journal currency={ccy} readOnly={archived} /></TabsContent>
        <TabsContent value="proposals" className="mt-4"><Proposals currency={ccy} /></TabsContent>
        <TabsContent value="transactions" className="mt-4"><Transactions currency={ccy} /></TabsContent>
        <TabsContent value="orders" className="mt-4"><Orders currency={ccy} /></TabsContent>
        <TabsContent value="cash" className="mt-4"><Cash currency={ccy} /></TabsContent>
      </Tabs>
    </>
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
            {isOpen && <CardContent className="border-t py-4"><TradeProposalView proposal={p} currency={currency} /></CardContent>}
          </Card>
        )
      })}
    </div>
  )
}

function Transactions({ currency }: { currency: string }) {
  const { portfolioId } = usePortfolioContext()
  const [page, setPage] = useState(1)
  const { data, isLoading, error } = useTransactions(portfolioId, page)
  if (isLoading) return <LoadingBlock />
  if (error) return <ErrorState error={error} />
  return (
    <TabCard>
      <DataTable
        caption="Simulated executions"
        rows={data?.items ?? []}
        rowKey={(t) => t.id}
        empty={<EmptyState title="No executions yet" />}
        columns={[
          { key: 't', header: 'Executed', cell: (t) => dateTime(t.executedAtUtc) },
          { key: 's', header: 'Symbol', cell: (t) => <span className="font-medium">{t.symbol}</span> },
          { key: 'side', header: 'Side', cell: (t) => <Badge variant="outline" className={t.side === 'Buy' ? 'text-gain' : 'text-loss'}>{t.side}</Badge> },
          { key: 'q', header: 'Quantity', align: 'right', cell: (t) => qty(t.quantity) },
          {
            key: 'p', header: 'Price (own currency)', align: 'right',
            cell: (t) => (
              <div>
                <span title={`Close of ${date(t.priceDate)}`}>{money(t.price, t.currency)}</span>
                {t.fxRate !== 1 && <div><FxNote rate={t.fxRate} from={t.currency} to={currency} on={t.fxRateDate} /></div>}
              </div>
            ),
          },
          { key: 'g', header: 'Gross', align: 'right', cell: (t) => money(t.grossAmount, currency) },
          { key: 'f', header: 'Fees', align: 'right', cell: (t) => money(t.fees, currency) },
          { key: 'n', header: 'Net cash flow', align: 'right', cell: (t) => <Signed value={t.netCashFlow}>{money(t.netCashFlow, currency, { signed: true })}</Signed> },
          { key: 'r', header: 'Realized P&L', align: 'right', cell: (t) => t.realizedPnl === null ? '—' : <Signed value={t.realizedPnl}>{money(t.realizedPnl, currency, { signed: true })}</Signed> },
        ]}
      />
      {data && <Pager page={page} totalPages={data.totalPages} onChange={setPage} />}
    </TabCard>
  )
}

function Orders({ currency }: { currency: string }) {
  const { portfolioId } = usePortfolioContext()
  const [page, setPage] = useState(1)
  const { data, isLoading, error } = useOrders(portfolioId, page)
  if (isLoading) return <LoadingBlock />
  if (error) return <ErrorState error={error} />
  return (
    <TabCard>
      <DataTable
        caption="Simulated orders"
        rows={data?.items ?? []}
        rowKey={(o) => o.id}
        empty={<EmptyState title="No orders yet" />}
        columns={[
          { key: 't', header: 'Created', cell: (o) => dateTime(o.createdAtUtc) },
          { key: 's', header: 'Symbol', cell: (o) => <span className="font-medium">{o.symbol}</span> },
          { key: 'side', header: 'Side', cell: (o) => o.side },
          { key: 'rq', header: 'Requested', align: 'right', cell: (o) => qty(o.requestedQuantity) },
          { key: 'fq', header: 'Filled', align: 'right', cell: (o) => qty(o.filledQuantity) },
          { key: 'px', header: 'Fill price', align: 'right', cell: (o) => <div>{money(o.executionPrice, o.currency)}{o.fxRate !== null && o.fxRate !== 1 && <div><FxNote rate={o.fxRate} from={o.currency} to={currency} /></div>}</div> },
          { key: 'st', header: 'Status', cell: (o) => <Badge variant={o.status === 'Rejected' ? 'destructive' : 'secondary'}>{o.status}</Badge> },
          { key: 'm', header: 'Message', cell: (o) => <span className="text-xs text-muted-foreground">{o.message}</span> },
        ]}
      />
      {data && <Pager page={page} totalPages={data.totalPages} onChange={setPage} />}
    </TabCard>
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
function TabCard({ children }: { children: ReactNode }) {
  return <Card className="py-4"><CardContent className="space-y-3 px-4">{children}</CardContent></Card>
}

const REVERSIBLE = new Set(['Deposit', 'Withdrawal', 'Dividend', 'Fee'])

/** Manually recorded transactions of this portfolio, newest first, with an audit-safe reversal for cash events. */
function Journal({ currency, readOnly }: { currency: string; readOnly: boolean }) {
  const { portfolioId } = usePortfolioContext()
  const [page, setPage] = useState(1)
  const journal = useJournal(portfolioId, page)
  const reverse = useReverseTransaction(portfolioId ?? '')
  const [pending, setPending] = useState<string | null>(null)

  async function onReverse(t: PortfolioTransaction) {
    setPending(t.id)
    try {
      // One key per click on this row: a repeated click replays instead of reversing twice.
      await reverse.mutateAsync({ transactionId: t.id, idempotencyKey: `reverse-${t.id}` })
      toast.success('Reversal recorded', { description: 'The original stays in the history; an opposite entry was added.' })
    } catch (e) {
      toast.error('Could not reverse', { description: errorMessage(e) })
    }
    setPending(null)
  }

  if (journal.error) return <ErrorState error={journal.error} />
  if (!journal.data) return <LoadingBlock />
  return (
    <Card className="py-4">
      <CardContent className="space-y-3 px-4">
        <p className="text-xs text-muted-foreground">
          Transactions you recorded with “Record transaction” (simulated bookkeeping, not proof of a real trade). Simulated fills from confirmed
          proposals appear under Executions. Trades are corrected by recording the opposite trade.
        </p>
        <DataTable
          rows={journal.data.items}
          rowKey={(t) => t.id}
          caption="Recorded transactions"
          columns={[
            { key: 'date', header: 'Date', sortValue: (t) => t.bookedAtUtc, cell: (t) => <span className="text-xs tabular">{date(t.effectiveDate)}</span> },
            {
              key: 'type', header: 'Type', cell: (t) => (
                <span className="flex flex-wrap items-center gap-1">
                  <Badge variant="outline">{t.type}</Badge>
                  {t.reversesId && <Badge variant="secondary">Reversal</Badge>}
                  {t.reversedById && <Badge variant="secondary">Reversed</Badge>}
                </span>
              ),
            },
            { key: 'symbol', header: 'Stock', cell: (t) => t.symbol ?? '—' },
            { key: 'qty', header: 'Shares', align: 'right', cell: (t) => (t.quantity == null ? '—' : qty(t.quantity)) },
            { key: 'price', header: 'Price', align: 'right', cell: (t) => (t.price == null ? '—' : <span title={t.fxSource ? `Exchange rate ${t.fxRate} (${t.fxSource}, ${t.fxRateDate})` : undefined}>{money(t.price, t.currency)}</span>) },
            { key: 'fees', header: 'Fees', align: 'right', cell: (t) => (t.fees ? money(t.fees, currency) : '—') },
            { key: 'cash', header: 'Cash effect', align: 'right', sortValue: (t) => t.cashAmount, cell: (t) => <Signed value={t.cashAmount}>{money(t.cashAmount, currency, { signed: true })}</Signed> },
            { key: 'pnl', header: 'Realized P&L', align: 'right', cell: (t) => (t.realizedPnl == null ? '—' : <Signed value={t.realizedPnl}>{money(t.realizedPnl, currency, { signed: true })}</Signed>) },
            { key: 'note', header: 'Note', cell: (t) => <span className="text-xs text-muted-foreground">{t.note ?? ''}</span>, className: 'hidden lg:table-cell' },
            {
              key: 'actions', header: '', cell: (t) => !readOnly && REVERSIBLE.has(t.type) && !t.reversesId && !t.reversedById ? (
                <Button size="xs" variant="ghost" disabled={pending === t.id} onClick={() => void onReverse(t)} aria-label={`Reverse the ${t.type.toLowerCase()} of ${date(t.effectiveDate)}`}>Reverse</Button>
              ) : null,
            },
          ]}
          empty={<EmptyState title="Nothing recorded yet" description="Use “Record transaction” to book a buy, sell, deposit, withdrawal, dividend or fee." />}
        />
        <Pager page={page} totalPages={journal.data.totalPages} onChange={setPage} />
      </CardContent>
    </Card>
  )
}
