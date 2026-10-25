import { useState } from 'react'
import { toast } from 'sonner'
import { Button } from '@/components/ui/button'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { errorMessage } from '@/lib/api'
import { date, money } from '@/lib/format'
import { useRecordTransaction, useStocks } from '@/lib/queries'
import type { PortfolioTransactionType, RecordTransactionRequest } from '@/lib/types'
import { MatrixLoader } from '@/components/matrix-loader'

const TYPES: { value: PortfolioTransactionType; label: string; help: string }[] = [
  { value: 'Buy', label: 'Buy', help: 'Shares bought at the price you paid. The commission is added to the cost basis.' },
  { value: 'Sell', label: 'Sell', help: 'Shares sold at the price you received. Realized P&L uses the weighted-average cost.' },
  { value: 'Deposit', label: 'Deposit cash', help: 'Money added to the portfolio. A contribution – never counted as a gain.' },
  { value: 'Withdrawal', label: 'Withdraw cash', help: 'Money taken out. Never counted as a loss; cannot exceed the cash balance.' },
  { value: 'Dividend', label: 'Dividend', help: 'Net cash dividend received for a stock this portfolio holds or has held. Counts as income.' },
  { value: 'Fee', label: 'Fee', help: 'A standalone charge (e.g. custody). Reduces return; cannot exceed the cash balance.' },
]

const today = () => new Date().toISOString().slice(0, 10)
const newKey = () => (typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : `${Date.now()}-${Math.random()}`)
const num = (v: string) => (v.trim() === '' ? undefined : Number(v))

/**
 * Records a manual transaction in ONE portfolio (named in the title). Bookings are simulated bookkeeping – no order is
 * sent anywhere. Each opened form has its own idempotency key, so a double submission books once.
 */
export function TransactionDialog({ portfolioId, portfolioName, currency, open, onOpenChange, symbols, initialType = 'Buy' }: {
  portfolioId: string
  portfolioName: string
  currency: string
  open: boolean
  onOpenChange: (open: boolean) => void
  /** Suggestions: held stocks and the working universe. */
  symbols: string[]
  initialType?: PortfolioTransactionType
}) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        {/* Remounted per opening: a fresh form and a fresh idempotency key. */}
        {open && <TransactionForm key={String(open)} portfolioId={portfolioId} portfolioName={portfolioName} currency={currency} symbols={symbols}
          initialType={initialType} onDone={() => onOpenChange(false)} />}
      </DialogContent>
    </Dialog>
  )
}

function TransactionForm({ portfolioId, portfolioName, currency, symbols, initialType, onDone }: {
  portfolioId: string
  portfolioName: string
  currency: string
  symbols: string[]
  initialType: PortfolioTransactionType
  onDone: () => void
}) {
  const record = useRecordTransaction(portfolioId)
  const [key] = useState(newKey)
  const [type, setType] = useState<PortfolioTransactionType>(initialType)
  const [symbol, setSymbol] = useState('')
  const [quantity, setQuantity] = useState('')
  const [price, setPrice] = useState('')
  const [amount, setAmount] = useState('')
  const [fee, setFee] = useState('')
  const [day, setDay] = useState(today())
  // Only a date the user chose is sent; otherwise the server books "today" by its own clock (a skewed browser clock
  // must not make a default booking look like a future one).
  const [dayChanged, setDayChanged] = useState(false)
  const [note, setNote] = useState('')
  const [error, setError] = useState<string | null>(null)

  const isTrade = type === 'Buy' || type === 'Sell'
  const needsSymbol = isTrade || type === 'Dividend'
  const sym = symbol.trim().toUpperCase()
  const stock = useStocks({ symbols: sym ? [sym] : [], pageSize: 1 }, needsSymbol && sym.length > 0)
  const match = stock.data?.items.find((s) => s.symbol === sym)
  const quote = match?.quote
  const priceCurrency = match?.currency ?? 'the stock’s currency'

  async function submit(e: React.FormEvent) {
    e.preventDefault()
    setError(null)
    const body: RecordTransactionRequest = {
      type,
      date: dayChanged && day ? day : undefined,
      note: note.trim() || undefined,
      ...(needsSymbol ? { symbol: sym || undefined } : {}),
      ...(isTrade ? { quantity: num(quantity), price: num(price), fee: num(fee) ?? 0 } : { amount: num(amount) }),
    }
    try {
      const t = await record.mutateAsync({ body, idempotencyKey: key })
      toast.success(`${TYPES.find((x) => x.value === type)!.label} recorded in “${portfolioName}”`, {
        description: `Cash ${money(t.cashAmount, currency, { signed: true })} · balance ${money(t.cashBalanceAfter, currency)}${t.idempotentReplay ? ' (already booked)' : ''}`,
      })
      onDone()
    } catch (err) {
      setError(errorMessage(err))
    }
  }

  return (
    <form onSubmit={submit} noValidate className="space-y-4">
      <DialogHeader>
        <DialogTitle>Record a transaction</DialogTitle>
        <DialogDescription>
          In <span className="font-medium text-foreground">{portfolioName}</span> ({currency}). Simulated bookkeeping: nothing is sent to a broker
          and no real money moves.
        </DialogDescription>
      </DialogHeader>

      <div className="grid gap-3 sm:grid-cols-2">
        <div className="space-y-1.5 sm:col-span-2">
          <Label htmlFor="tx-type">Type</Label>
          <Select value={type} onValueChange={(v) => { setType(v as PortfolioTransactionType); setError(null) }} items={TYPES.map((t) => ({ value: t.value, label: t.label }))}>
            <SelectTrigger id="tx-type" className="w-full" aria-label="Transaction type"><SelectValue /></SelectTrigger>
            <SelectContent>{TYPES.map((t) => <SelectItem key={t.value} value={t.value}>{t.label}</SelectItem>)}</SelectContent>
          </Select>
          <p className="text-xs text-muted-foreground">{TYPES.find((t) => t.value === type)!.help}</p>
        </div>

        {needsSymbol && (
          <div className="space-y-1.5 sm:col-span-2">
            <Label htmlFor="tx-symbol">Stock</Label>
            <Input id="tx-symbol" list="tx-symbols" autoComplete="off" placeholder="Ticker, e.g. AAPL" value={symbol} onChange={(e) => setSymbol(e.target.value)} />
            <datalist id="tx-symbols">{symbols.map((s) => <option key={s} value={s} />)}</datalist>
            {isTrade && quote?.price != null && (
              <p className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                Latest stored price {money(quote.price, quote.currency)} ({quote.kind === 'DelayedQuote' ? 'delayed quote' : 'last close'} {date(quote.priceDate)}{quote.isSimulated ? ', SIMULATED' : ''})
                <Button type="button" size="xs" variant="outline" onClick={() => setPrice(String(quote.price))}>Use it</Button>
              </p>
            )}
            {sym && stock.data && !match && <p className="text-xs text-destructive">Unknown ticker {sym}.</p>}
          </div>
        )}

        {isTrade ? (
          <>
            <div className="space-y-1.5">
              <Label htmlFor="tx-qty">Shares</Label>
              <Input id="tx-qty" type="number" inputMode="decimal" min="0" step="any" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="tx-price">Price per share ({match?.currency ?? '…'})</Label>
              <Input id="tx-price" type="number" inputMode="decimal" min="0" step="any" value={price} onChange={(e) => setPrice(e.target.value)} aria-describedby="tx-price-help" />
              <p id="tx-price-help" className="text-[11px] text-muted-foreground">The price you traded at, in {priceCurrency}.</p>
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="tx-fee">Commission ({currency})</Label>
              <Input id="tx-fee" type="number" inputMode="decimal" min="0" step="any" placeholder="0" value={fee} onChange={(e) => setFee(e.target.value)} />
            </div>
          </>
        ) : (
          <div className="space-y-1.5">
            <Label htmlFor="tx-amount">Amount ({currency})</Label>
            <Input id="tx-amount" type="number" inputMode="decimal" min="0" step="any" value={amount} onChange={(e) => setAmount(e.target.value)} />
          </div>
        )}

        <div className="space-y-1.5">
          <Label htmlFor="tx-date">Date</Label>
          <Input id="tx-date" type="date" max={today()} value={day} onChange={(e) => { setDay(e.target.value); setDayChanged(true) }} aria-describedby="tx-date-help" />
          <p id="tx-date-help" className="text-[11px] text-muted-foreground">Not before the portfolio’s latest booking.</p>
        </div>
        <div className="space-y-1.5 sm:col-span-2">
          <Label htmlFor="tx-note">Note (optional)</Label>
          <Input id="tx-note" maxLength={500} value={note} onChange={(e) => setNote(e.target.value)} />
        </div>
      </div>

      {error && <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/5 px-3 py-2 text-sm text-destructive">{error}</p>}

      <DialogFooter>
        <Button type="button" variant="outline" onClick={onDone}>Cancel</Button>
        <Button type="submit" disabled={record.isPending}>{record.isPending && <MatrixLoader />} Record</Button>
      </DialogFooter>
    </form>
  )
}
