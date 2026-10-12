import { useState } from 'react'
import { toast } from 'sonner'
import { ChevronDown, ChevronRight, RefreshCcw } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent } from '@/components/ui/card'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { DataTable, Pager } from '@/components/data-table'
import { EmptyState, ErrorState, LoadingBlock, PageHeader, Signed } from '@/components/common'
import { ProposalStatusBadge, TradeProposalView } from '@/components/proposal'
import { errorMessage } from '@/lib/api'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useCashLedger, useOrders, useProposals, useRebalancePreview, useTransactions } from '@/lib/queries'
import { date, dateTime, money, qty } from '@/lib/format'
import { NoPortfolio } from './shared'

export function TradesPage() {
  const { portfolioId, portfolio, isLoading } = usePortfolioContext()
  const ccy = portfolio?.baseCurrency ?? 'USD'
  const rebalance = useRebalancePreview(portfolioId ?? '')
  const [tab, setTab] = useState('proposals')

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
      <PageHeader
        title="Trades & ledger"
        description="Trade proposals, simulated orders, executions and the cash ledger. History is append-only."
        actions={<Button variant="outline" onClick={onRebalance} disabled={rebalance.isPending}><RefreshCcw /> Rebalance to latest run</Button>}
      />
      <Tabs value={tab} onValueChange={(v) => setTab(String(v))}>
        <TabsList>
          <TabsTrigger value="proposals">Proposals</TabsTrigger>
          <TabsTrigger value="transactions">Executions</TabsTrigger>
          <TabsTrigger value="orders">Orders</TabsTrigger>
          <TabsTrigger value="cash">Cash ledger</TabsTrigger>
        </TabsList>
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
    return <EmptyState title="No trade proposals" description="Run an optimization and create a trade proposal, or rebalance to the latest run." />
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
    <>
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
          { key: 'p', header: 'Price', align: 'right', cell: (t) => <span title={`Close of ${date(t.priceDate)}`}>{money(t.price, currency)}</span> },
          { key: 'g', header: 'Gross', align: 'right', cell: (t) => money(t.grossAmount, currency) },
          { key: 'f', header: 'Fees', align: 'right', cell: (t) => money(t.fees, currency) },
          { key: 'n', header: 'Net cash flow', align: 'right', cell: (t) => <Signed value={t.netCashFlow}>{money(t.netCashFlow, currency, { signed: true })}</Signed> },
          { key: 'r', header: 'Realized P&L', align: 'right', cell: (t) => t.realizedPnl === null ? '—' : <Signed value={t.realizedPnl}>{money(t.realizedPnl, currency, { signed: true })}</Signed> },
        ]}
      />
      {data && <Pager page={page} totalPages={data.totalPages} onChange={setPage} />}
    </>
  )
}

function Orders({ currency }: { currency: string }) {
  const { portfolioId } = usePortfolioContext()
  const [page, setPage] = useState(1)
  const { data, isLoading, error } = useOrders(portfolioId, page)
  if (isLoading) return <LoadingBlock />
  if (error) return <ErrorState error={error} />
  return (
    <>
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
          { key: 'px', header: 'Fill price', align: 'right', cell: (o) => money(o.executionPrice, currency) },
          { key: 'st', header: 'Status', cell: (o) => <Badge variant={o.status === 'Rejected' ? 'destructive' : 'secondary'}>{o.status}</Badge> },
          { key: 'm', header: 'Message', cell: (o) => <span className="text-xs text-muted-foreground">{o.message}</span> },
        ]}
      />
      {data && <Pager page={page} totalPages={data.totalPages} onChange={setPage} />}
    </>
  )
}

function Cash({ currency }: { currency: string }) {
  const { portfolioId } = usePortfolioContext()
  const [page, setPage] = useState(1)
  const { data, isLoading, error } = useCashLedger(portfolioId, page)
  if (isLoading) return <LoadingBlock />
  if (error) return <ErrorState error={error} />
  return (
    <>
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
    </>
  )
}
