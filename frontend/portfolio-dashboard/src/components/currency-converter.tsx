import { useMemo, useState } from 'react'
import { ArrowLeftRight, Coins } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { TimeSeriesChart } from '@/components/charts'
import { DataBadge, ErrorState, LoadingBlock, Signed } from '@/components/common'
import { converterCurrencies, convertAmount, crossFromBoard, fxPhrase } from '@/lib/currency'
import { date, pct } from '@/lib/format'
import { useFxBoard, useFxHistory, useFxInfo } from '@/lib/queries'
import { cn } from '@/lib/utils'

const ranges = [
  { label: '1M', days: 31 },
  { label: '3M', days: 92 },
  { label: '1Y', days: 366 },
  { label: '5Y', days: 1830 },
] as const

/**
 * Currency converter: published reference rates of the major currencies against `base`, a chart of the selected pair and a
 * calculator. Rates are daily reference rates (ECB, or simulated with simulated prices) – never live, never an exchange offer.
 */
export function CurrencyConverterCard({ base, className }: { base: string; className?: string }) {
  const info = useFxInfo()
  // The board's base ("1 USD =") is selectable among all published currencies; it defaults to the portfolio currency.
  const [boardBase, setBoardBase] = useState<string>()
  const b = boardBase ?? base
  const allCurrencies = useMemo(() => [...(info.data?.currencies ?? [base])].sort(), [info.data?.currencies, base])
  const currencies = useMemo(() => [...new Set([b, ...converterCurrencies(base, info.data?.currencies)])], [b, base, info.data?.currencies])
  const quotes = currencies.filter((c) => c !== b)
  const board = useFxBoard(b, quotes)
  const [pair, setPair] = useState<string>()
  const chartQuote = pair && quotes.includes(pair) ? pair : quotes[0]
  const [range, setRange] = useState<(typeof ranges)[number]['label']>('1Y')
  const history = useFxHistory(b, chartQuote, ranges.find((r) => r.label === range)!.days)

  const [amount, setAmount] = useState('1000')
  const [from, setFrom] = useState<string>()
  const [to, setTo] = useState<string>()
  // Default pair: the chosen board base (or the first major) → the portfolio currency, never X → X.
  const fromCcy = from ?? (b !== base ? b : quotes[0]) ?? base
  const toCcy = to ?? base
  const rates = useMemo(() => Object.fromEntries((board.data?.rows ?? []).map((r) => [r.quote, r.rate])), [board.data])
  const rate = crossFromBoard(rates, b, fromCcy, toCcy)
  const result = convertAmount(Number(amount.replace(',', '.')), rate)
  const rateDates = (board.data?.rows ?? []).filter((r) => r.quote === fromCcy || r.quote === toCcy).map((r) => r.observationDate).filter((d): d is string => !!d)
  const asOf = rateDates.length ? rateDates.sort()[0] : board.data?.rows.find((r) => r.observationDate)?.observationDate ?? null
  const missing = (board.data?.rows ?? []).filter((r) => r.problem && (r.quote === fromCcy || r.quote === toCcy))

  const items = currencies.map((c) => ({ value: c, label: c }))

  return (
    <Card className={className} role="region" aria-label="Currency converter">
      <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-2">
        <CardTitle className="flex items-center gap-2 text-sm"><Coins className="size-4" aria-hidden /> Currency converter</CardTitle>
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          {board.data?.isSimulated ? <DataBadge kind="simulated" /> : board.data && <Badge variant="outline">Reference rates</Badge>}
          {asOf && <span>as of {date(asOf)}</span>}
        </div>
      </CardHeader>
      <CardContent className="space-y-5">
        {board.error && <ErrorState error={board.error} onRetry={() => board.refetch()} />}

        {/* Calculator */}
        <div className="grid items-end gap-3 sm:grid-cols-[minmax(0,1fr)_7rem_auto_7rem]">
          <div className="space-y-1.5">
            <Label htmlFor="fx-amount">Amount</Label>
            <Input id="fx-amount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-label="Amount to convert" />
          </div>
          <div className="space-y-1.5">
            <Label>From</Label>
            <Select value={fromCcy} onValueChange={(v) => setFrom(v as string)} items={items}>
              <SelectTrigger className="w-full" aria-label="Convert from"><SelectValue /></SelectTrigger>
              <SelectContent>{currencies.map((c) => <SelectItem key={c} value={c}>{c}</SelectItem>)}</SelectContent>
            </Select>
          </div>
          <Button variant="ghost" size="icon" aria-label="Swap currencies" onClick={() => { setFrom(toCcy); setTo(fromCcy) }}><ArrowLeftRight /></Button>
          <div className="space-y-1.5">
            <Label>To</Label>
            <Select value={toCcy} onValueChange={(v) => setTo(v as string)} items={items}>
              <SelectTrigger className="w-full" aria-label="Convert to"><SelectValue /></SelectTrigger>
              <SelectContent>{currencies.map((c) => <SelectItem key={c} value={c}>{c}</SelectItem>)}</SelectContent>
            </Select>
          </div>
        </div>
        <div className="rounded-lg bg-muted/50 px-4 py-3" aria-live="polite">
          {board.isLoading ? <span className="text-sm text-muted-foreground">Loading rates…</span> : result !== null ? (
            <>
              <div className="text-2xl font-semibold tabular" aria-label="Converted amount">
                {new Intl.NumberFormat('en-US', { style: 'currency', currency: toCcy, maximumFractionDigits: toCcy === 'JPY' ? 0 : 2 }).format(result)}
              </div>
              <div className="mt-0.5 text-xs text-muted-foreground">{fxPhrase(rate, fromCcy, toCcy)}{asOf ? ` · reference rate of ${date(asOf)}` : ''}</div>
            </>
          ) : (
            <span className="text-sm text-warn">
              {missing.length ? missing.map((m) => `${m.quote}: ${m.problem}`).join(' ') : 'Enter a number to convert.'}
            </span>
          )}
        </div>

        <div className="grid gap-4 lg:grid-cols-[minmax(0,14rem)_minmax(0,1fr)]">
          {/* Rate board */}
          <div>
            <div className="mb-2 flex items-center gap-2">
              <span className="text-xs font-medium text-muted-foreground uppercase">1</span>
              <Select value={b} onValueChange={(v) => setBoardBase(v as string)} items={allCurrencies.map((c) => ({ value: c, label: c }))}>
                <SelectTrigger size="sm" className="w-24" aria-label="Base currency of the rates"><SelectValue /></SelectTrigger>
                <SelectContent>{allCurrencies.map((c) => <SelectItem key={c} value={c}>{c}</SelectItem>)}</SelectContent>
              </Select>
              <span className="text-xs font-medium text-muted-foreground uppercase">=</span>
            </div>
            {board.isLoading ? <LoadingBlock height="h-40" /> : (
              <ul className="space-y-1" aria-label={`Exchange rates against ${b}`}>
                {(board.data?.rows ?? []).map((r) => (
                  <li key={r.quote}>
                    <button type="button" onClick={() => setPair(r.quote)} aria-pressed={r.quote === chartQuote}
                      className={cn('grid w-full grid-cols-[3rem_1fr_4rem] items-center gap-2 rounded-md px-2 py-1 text-left text-sm hover:bg-muted/60',
                        r.quote === chartQuote && 'bg-muted')}>
                      <span className="font-medium">{r.quote}</span>
                      <span className="text-right tabular">{r.rate === null ? '—' : r.rate.toLocaleString('en-US', { maximumFractionDigits: r.rate >= 100 ? 2 : 4 })}</span>
                      <span className="text-right text-xs tabular" title="Change since the previous published rate">
                        {r.change1D === null ? '—' : <Signed value={r.change1D}>{pct(r.change1D, 2, true)}</Signed>}
                      </span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>

          {/* Chart of the selected pair */}
          <div className="min-w-0">
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <h3 className="text-xs font-medium text-muted-foreground uppercase">{chartQuote} per {b}</h3>
              <div role="radiogroup" aria-label="Exchange-rate range" className="flex gap-1">
                {ranges.map((r) => (
                  <Button key={r.label} size="xs" variant={r.label === range ? 'secondary' : 'ghost'} role="radio" aria-checked={r.label === range} onClick={() => setRange(r.label)}>
                    {r.label}
                  </Button>
                ))}
              </div>
            </div>
            {history.isLoading ? <LoadingBlock height="h-48" /> : history.data && history.data.points.length > 1 ? (
              <>
                <TimeSeriesChart
                  dates={history.data.points.map((p) => p.date)}
                  series={[{ name: `${chartQuote} per ${b}`, values: history.data.points.map((p) => p.rate) }]}
                  format={(v) => v.toLocaleString('en-US', { maximumFractionDigits: v >= 100 ? 1 : 4 })}
                  yDomain={['auto', 'auto']}
                  height={190}
                />
                <p className="mt-1 text-xs text-muted-foreground">
                  {range} change: <Signed value={history.data.points.at(-1)!.rate / history.data.points[0].rate - 1}>
                    {pct(history.data.points.at(-1)!.rate / history.data.points[0].rate - 1, 2, true)}
                  </Signed>{' '}({history.data.points.length} published rates)
                </p>
              </>
            ) : <p className="text-sm text-muted-foreground">No published rates for this pair in the period.</p>}
          </div>
        </div>

        <p className="text-xs text-muted-foreground">
          {board.data?.note ?? 'Reference rates, published once per business day.'} For analysis and reporting only – not a rate at which money can be
          exchanged; banks and brokers add spreads and fees.
        </p>
      </CardContent>
    </Card>
  )
}
