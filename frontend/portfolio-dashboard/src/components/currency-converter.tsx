import { useMemo, useState } from 'react'
import { ArrowLeftRight, Coins, X } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { TimeSeriesChart } from '@/components/charts'
import { DataBadge, ErrorState, LoadingBlock, Signed } from '@/components/common'
import { CurrencySelect } from '@/components/currency'
import { converterCurrencies, convertAmount, crossFromBoard, currencyName, fxPhrase } from '@/lib/currency'
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
 * Currency converter: published reference rates against a selectable base (the majors plus any currency the user adds), a
 * chart of any pair and a calculator over every supported currency. Rates are daily reference rates (ECB, or simulated with simulated prices) – never live, never an exchange offer.
 */
export function CurrencyConverterCard({ base, className }: { base: string; className?: string }) {
  const info = useFxInfo()
  // The board's base ("1 USD =") is selectable among all published currencies; it defaults to the portfolio currency.
  const [boardBase, setBoardBase] = useState<string>()
  const b = boardBase ?? base
  const supported = info.data?.currencies
  // Board rows: the supported majors plus the currencies the user added (any supported one) – kept for this visit only.
  const [added, setAdded] = useState<string[]>([])
  const quotes = useMemo(() => [...new Set([...converterCurrencies(base, supported), ...added])].filter((c) => c !== b), [base, supported, added, b])
  const board = useFxBoard(b, quotes)
  const [pair, setPair] = useState<string>()
  const chartQuote = pair && pair !== b ? pair : quotes[0]
  const [range, setRange] = useState<(typeof ranges)[number]['label']>('1Y')
  const history = useFxHistory(b, chartQuote, ranges.find((r) => r.label === range)!.days)

  const [amount, setAmount] = useState('1000')
  const [from, setFrom] = useState<string>()
  const [to, setTo] = useState<string>()
  // Default pair: the chosen board base (or the first major) → the portfolio currency, never X → X.
  const fromCcy = from ?? (b !== base ? b : quotes[0]) ?? base
  const toCcy = to ?? base
  // The calculator may use any supported pair, so it asks for its own two rates against the board's base (cross via `b`).
  const pairBoard = useFxBoard(b, [fromCcy, toCcy].filter((c) => c !== b))
  const pairRows = useMemo(() => (fromCcy === b && toCcy === b ? [] : pairBoard.data?.rows ?? []), [fromCcy, toCcy, b, pairBoard.data])
  const rates = useMemo(() => Object.fromEntries(pairRows.map((r) => [r.quote, r.rate])), [pairRows])
  const rate = crossFromBoard(rates, b, fromCcy, toCcy)
  const result = convertAmount(Number(amount.replace(',', '.')), rate)
  const rateDates = pairRows.filter((r) => r.quote === fromCcy || r.quote === toCcy).map((r) => r.observationDate).filter((d): d is string => !!d)
  const asOf = rateDates.length ? rateDates.sort()[0] : board.data?.rows.find((r) => r.observationDate)?.observationDate ?? null
  const missing = pairRows.filter((r) => r.problem && (r.quote === fromCcy || r.quote === toCcy))
  const addable = (supported ?? []).filter((c) => c !== b && !quotes.includes(c))

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
        <div className="grid items-end gap-3 sm:grid-cols-[minmax(0,1fr)_8rem_auto_8rem]">
          <div className="space-y-1.5">
            <Label htmlFor="fx-amount">Amount</Label>
            <Input id="fx-amount" inputMode="decimal" value={amount} onChange={(e) => setAmount(e.target.value)} aria-label="Amount to convert" />
          </div>
          <div className="space-y-1.5">
            <Label>From</Label>
            <CurrencySelect value={fromCcy} onChange={setFrom} supported={supported} first={base} label="Convert from" className="w-full" />
          </div>
          <Button variant="ghost" size="icon" aria-label="Swap currencies" onClick={() => { setFrom(toCcy); setTo(fromCcy) }}><ArrowLeftRight /></Button>
          <div className="space-y-1.5">
            <Label>To</Label>
            <CurrencySelect value={toCcy} onChange={setTo} supported={supported} first={base} label="Convert to" className="w-full" />
          </div>
        </div>
        <div className="rounded-lg bg-muted/50 px-4 py-3" aria-live="polite">
          {pairBoard.isLoading ? <span className="text-sm text-muted-foreground">Loading rates…</span> : result !== null ? (
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
              <CurrencySelect value={b} onChange={setBoardBase} supported={supported} first={base} label="Base currency of the rates" size="sm" className="w-24" />
              <span className="text-xs font-medium text-muted-foreground uppercase">=</span>
            </div>
            {board.isLoading ? <LoadingBlock height="h-40" /> : (
              <ul className="space-y-1" aria-label={`Exchange rates against ${b}`}>
                {(board.data?.rows ?? []).map((r) => (
                  <li key={r.quote} className="group flex items-center gap-1">
                    <button type="button" onClick={() => setPair(r.quote)} aria-pressed={r.quote === chartQuote} title={currencyName(r.quote)}
                      className={cn('grid min-w-0 flex-1 grid-cols-[3rem_1fr_4rem] items-center gap-2 rounded-md px-2 py-1 text-left text-sm hover:bg-muted/60',
                        r.quote === chartQuote && 'bg-muted')}>
                      <span className="font-medium">{r.quote}</span>
                      <span className="text-right tabular">{r.rate === null ? '—' : r.rate.toLocaleString('en-US', { maximumFractionDigits: r.rate >= 100 ? 2 : 4 })}</span>
                      <span className="text-right text-xs tabular" title="Change since the previous published rate">
                        {r.change1D === null ? '—' : <Signed value={r.change1D}>{pct(r.change1D, 2, true)}</Signed>}
                      </span>
                    </button>
                    {added.includes(r.quote) && (
                      <Button variant="ghost" size="icon-xs" aria-label={`Remove ${r.quote}`} onClick={() => setAdded(added.filter((c) => c !== r.quote))}><X /></Button>
                    )}
                  </li>
                ))}
              </ul>
            )}
            {addable.length > 0 && (
              <CurrencySelect value={null} onChange={(c) => { setAdded([...added, c]); setPair(c) }} supported={addable}
                label="Add a currency to the rates" placeholder="+ Add currency" size="sm" className="mt-2 w-full" />
            )}
          </div>

          {/* Chart of the selected pair */}
          <div className="min-w-0">
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <div className="flex items-center gap-2">
                {chartQuote && <CurrencySelect value={chartQuote} onChange={setPair} supported={supported} first={base} exclude={b} label="Currency shown in the chart" size="sm" className="w-24" />}
                <h3 className="text-xs font-medium text-muted-foreground uppercase">per {b}</h3>
              </div>
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
