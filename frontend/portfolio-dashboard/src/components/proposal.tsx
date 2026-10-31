import { useId, useState, type ReactNode } from 'react'
import { ChevronDown } from 'lucide-react'
import { useNavigate } from 'react-router'
import { toast } from 'sonner'
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent, AlertDialogDescription, AlertDialogFooter,
  AlertDialogHeader, AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { DataTable, type Column } from '@/components/data-table'
import { MetricCard } from '@/components/common'
import { FxNote } from '@/components/currency'
import { errorMessage } from '@/lib/api'
import { useExecuteProposal } from '@/lib/queries'
import { MatrixLoader } from '@/components/matrix-loader'
import type { ProposalLine, TradeProposal } from '@/lib/types'
import { date, dateTime, money, pct, qty } from '@/lib/format'
import { cn } from '@/lib/utils'

const actionStyle: Record<string, string> = {
  Buy: 'border-gain/40 text-gain',
  Sell: 'border-loss/40 text-loss',
  Hold: 'text-muted-foreground',
  NoPrice: 'border-warn/50 text-warn',
}

export function ProposalStatusBadge({ status }: { status: TradeProposal['status'] }) {
  const cls = status === 'Pending' ? 'border-series-1/50 text-foreground' : status === 'Executed' ? 'border-gain/40 text-gain' : 'text-muted-foreground'
  return <Badge variant="outline" className={cls}>{status === 'Pending' ? 'Proposed (not executed)' : status}</Badge>
}

/**
 * The trade-proposal card of Math and ML Optimization: a button creates (or recalculates) the proposal, which opens
 * below; the title folds it away so it does not cover the results, leaving a one-line summary.
 */
export function TradeProposalCard({ description, proposal, currency, pending, disabled = false, onCreate }: {
  description: ReactNode; proposal: TradeProposal | null; currency: string; pending: boolean; disabled?: boolean; onCreate: () => Promise<void>
}) {
  const [open, setOpen] = useState(true)
  const id = useId()
  const orders = proposal?.lines.filter((l) => l.action === 'Buy' || l.action === 'Sell').length ?? 0
  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between gap-2">
        <div className="min-w-0">
          <CardTitle className="text-sm">
            {proposal ? (
              <button type="button" aria-expanded={open} aria-controls={id} onClick={() => setOpen((o) => !o)}
                className="flex items-center gap-1.5 text-left outline-none focus-visible:ring-2 focus-visible:ring-ring/50">
                <ChevronDown className={cn('size-4 shrink-0 text-muted-foreground transition-transform', open && 'rotate-180')} aria-hidden />
                Trade proposal
                {!open && (
                  <span className="text-xs font-normal text-muted-foreground">
                    ({orders} {orders === 1 ? 'order' : 'orders'} · buys {money(proposal.totalBuys, currency)} · sells {money(proposal.totalSells, currency)})
                  </span>
                )}
              </button>
            ) : 'Trade proposal'}
          </CardTitle>
          <CardDescription className="text-xs">{description}</CardDescription>
        </div>
        <Button variant={proposal ? 'outline' : 'default'} disabled={pending || disabled} onClick={async () => { await onCreate(); setOpen(true) }}>
          {pending && <MatrixLoader />}
          {proposal ? 'Recalculate' : 'Create trade proposal'}
        </Button>
      </CardHeader>
      {proposal && open && (
        <CardContent id={id}>
          <TradeProposalView proposal={proposal} currency={currency} />
        </CardContent>
      )}
    </Card>
  )
}

export function TradeProposalView({ proposal, currency, allowExecute = true }: { proposal: TradeProposal; currency: string; allowExecute?: boolean }) {
  const [confirmOpen, setConfirmOpen] = useState(false)
  // One idempotency key per proposal view: a double click or network retry can never execute twice.
  const [idempotencyKey] = useState(() => crypto.randomUUID())
  const execute = useExecuteProposal(proposal.portfolioId)
  const navigate = useNavigate()
  const trades = proposal.lines.filter((l) => l.action === 'Buy' || l.action === 'Sell')

  const columns: Column<ProposalLine>[] = [
    {
      key: 'symbol', header: 'Symbol', sortValue: (l) => l.symbol,
      cell: (l) => (
        <div>
          <div className="font-medium">{l.symbol}</div>
          <div className="max-w-40 truncate text-xs text-muted-foreground">{l.companyName}</div>
        </div>
      ),
    },
    { key: 'action', header: 'Action', sortValue: (l) => l.action, cell: (l) => <Badge variant="outline" className={actionStyle[l.action]}>{l.action === 'NoPrice' ? 'No price' : l.action}</Badge> },
    {
      key: 'price', header: 'Price', align: 'right', sortValue: (l) => l.price,
      cell: (l) => (
        <div>
          <div>{money(l.price, l.currency || currency)}</div>
          <div className={cn('text-xs', l.priceIsStale ? 'text-warn' : 'text-muted-foreground')}>{l.priceDate ? date(l.priceDate) : ''}{l.priceIsStale ? ' · stale' : ''}</div>
          {l.fxRate !== null && l.fxRate !== 1 && <div><FxNote rate={l.fxRate} from={l.currency} to={currency} on={l.fxRateDate} /></div>}
        </div>
      ),
    },
    { key: 'tw', header: 'Target wt', align: 'right', sortValue: (l) => l.targetWeight, cell: (l) => pct(l.targetWeight) },
    { key: 'cw', header: 'Current wt', align: 'right', sortValue: (l) => l.currentWeight, cell: (l) => pct(l.currentWeight) },
    { key: 'tv', header: 'Target value', align: 'right', sortValue: (l) => l.targetValue, cell: (l) => money(l.targetValue, currency) },
    { key: 'cq', header: 'Current qty', align: 'right', sortValue: (l) => l.currentQuantity, cell: (l) => qty(l.currentQuantity) },
    { key: 'tq', header: 'Target qty', align: 'right', sortValue: (l) => l.targetQuantity, cell: (l) => qty(l.targetQuantity) },
    { key: 'oq', header: 'Order qty', align: 'right', sortValue: (l) => l.orderQuantity, cell: (l) => <span className="font-medium">{l.orderQuantity > 0 ? `${l.action === 'Sell' ? '−' : '+'}${qty(l.orderQuantity)}` : '—'}</span> },
    { key: 'val', header: 'Trade value', align: 'right', sortValue: (l) => l.estimatedTradeValue, cell: (l) => money(l.estimatedTradeValue, currency) },
    { key: 'cost', header: 'Est. cost', align: 'right', sortValue: (l) => l.estimatedCost, cell: (l) => money(l.estimatedCost, currency) },
    { key: 'rw', header: 'Resulting wt', align: 'right', sortValue: (l) => l.resultingWeight, cell: (l) => pct(l.resultingWeight) },
  ]

  async function onConfirm() {
    try {
      const result = await execute.mutateAsync({ proposalId: proposal.id, idempotencyKey })
      const filled = result.orders.filter((o) => o.status !== 'Rejected').length
      toast.success(`Simulated execution complete: ${filled} order${filled === 1 ? '' : 's'} filled`, {
        description: `Fees ${money(result.totalFees, currency)} · cash after ${money(result.cashAfter, currency)}`,
      })
      result.notes.forEach((n) => toast.info(n))
      setConfirmOpen(false)
      navigate(`/portfolios/${proposal.portfolioId}`)
    } catch (e) {
      toast.error('Simulated execution failed', { description: errorMessage(e) })
      setConfirmOpen(false)
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <ProposalStatusBadge status={proposal.status} />
        <span className="text-muted-foreground">Created {dateTime(proposal.createdAtUtc)} · {proposal.allowFractionalShares ? 'fractional shares' : 'whole shares'}</span>
      </div>

      <div className="grid grid-cols-2 gap-3 md:grid-cols-5">
        <MetricCard label="Portfolio value" value={money(proposal.portfolioValue, currency)} />
        <MetricCard label="Buys" value={money(proposal.totalBuys, currency)} sub={`${proposal.lines.filter((l) => l.action === 'Buy').length} orders`} />
        <MetricCard label="Sells" value={money(proposal.totalSells, currency)} sub={`${proposal.lines.filter((l) => l.action === 'Sell').length} orders`} />
        <MetricCard label="Estimated costs" value={money(proposal.estimatedCosts, currency)} />
        <MetricCard label="Cash after trades" value={money(proposal.estimatedCashAfter, currency)} sub={`${pct(proposal.resultingCashWeight)} vs ${pct(proposal.targetCashWeight)} target`} />
      </div>

      {proposal.notes.length > 0 && (
        <Alert>
          <AlertTitle>Why the result deviates from target weights</AlertTitle>
          <AlertDescription>
            <ul className="list-disc space-y-0.5 pl-4">{proposal.notes.map((n) => <li key={n}>{n}</li>)}</ul>
          </AlertDescription>
        </Alert>
      )}

      <DataTable caption="Proposed trades" rows={proposal.lines} columns={columns} rowKey={(l) => l.symbol} dense />
      {proposal.lines.some((l) => l.note) && (
        <ul className="space-y-0.5 text-xs text-muted-foreground">
          {proposal.lines.filter((l) => l.note).map((l) => <li key={l.symbol}><span className="font-medium text-foreground">{l.symbol}:</span> {l.note}</li>)}
        </ul>
      )}

      {allowExecute && proposal.status === 'Pending' && (
        <div className="flex flex-col gap-3 rounded-lg border bg-muted/40 p-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex items-start gap-2 text-sm">
            <span>This is a <strong>proposal</strong>. Nothing has been bought. Executing creates <strong>simulated</strong> orders in the paper portfolio at the latest stored prices – no real orders are sent.</span>
          </div>
          <Button disabled={trades.length === 0 || execute.isPending} onClick={() => setConfirmOpen(true)}>
            Review & execute ({trades.length})
          </Button>
        </div>
      )}

      <AlertDialog open={confirmOpen} onOpenChange={setConfirmOpen}>
        <AlertDialogContent className="data-[size=default]:max-w-[calc(100%-2rem)] data-[size=default]:sm:max-w-lg">
          <AlertDialogHeader>
            <AlertDialogTitle>Execute {trades.length} simulated trade{trades.length === 1 ? '' : 's'}?</AlertDialogTitle>
            <AlertDialogDescription>
              Paper trading only. Holdings and cash of the simulated portfolio will be updated and recorded in the immutable ledger.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <div className="max-h-56 overflow-y-auto rounded-md border text-sm">
            <table className="w-full tabular">
              <tbody>
                {trades.map((l) => (
                  <tr key={l.symbol} className="border-b last:border-0">
                    <td className="px-3 py-1.5"><Badge variant="outline" className={actionStyle[l.action]}>{l.action}</Badge></td>
                    <td className="px-3 py-1.5 font-medium">{l.symbol}</td>
                    <td className="px-3 py-1.5 text-right">{qty(l.orderQuantity)} × {money(l.price, l.currency || currency)}{l.fxRate !== null && l.fxRate !== 1 && <> × {l.fxRate.toPrecision(5)}</>}</td>
                    <td className="px-3 py-1.5 text-right">{money(l.estimatedTradeValue, currency)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="flex justify-between text-sm">
            <span className="text-muted-foreground">Estimated costs</span><span className="tabular">{money(proposal.estimatedCosts, currency)}</span>
          </div>
          <div className="flex justify-between text-sm">
            <span className="text-muted-foreground">Estimated cash after</span><span className="tabular font-medium">{money(proposal.estimatedCashAfter, currency)}</span>
          </div>
          <AlertDialogFooter>
            <AlertDialogCancel disabled={execute.isPending}>Cancel</AlertDialogCancel>
            <AlertDialogAction disabled={execute.isPending} onClick={onConfirm}>
              {execute.isPending ? 'Executing…' : 'Confirm simulated execution'}
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </div>
  )
}
