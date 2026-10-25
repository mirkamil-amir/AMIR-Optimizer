import { useMemo, useState } from 'react'
import { ArrowLeftRight, Coins, X } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { TimeSeriesChart } from '@/components/charts'
import { DataBadge, ErrorState, InfoTip, LoadingBlock, Signed } from '@/components/common'
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
 * chart of any pair and a calculator over every supported currency. Rates are daily reference rates (ECB, or simulated with
 * simulated prices) – never live, never an exchange offer.
 *
 * Data flow: ONE board request per base loads every supported currency; the visible rows, the calculator and "Add currency"
 * all read from it, so adding or removing a currency changes only local state (no request). Only the chart asks for the
 * chosen pair's history; while it loads, the previous chart stays visible.
 */
export function CurrencyConverterCard({ base, className }: { base: string; className?: string }) {
  const info = useFxInfo()
  // The board's base ("1 USD =") is selectable among all published currencies; it defaults to the portfolio currency.
  const [boardBase, setBoardBase] = useState<string>()
  const b = boardBase ?? base
  const supported = info.data?.currencies
  const historical = useMemo(() => info.data?.discontinued ?? [], [info.data?.discontinued])
  const allQuotes = useMemo(() => (supported ?? []).filter((c) => c !== b).sort(), [supported, b])
  const board = useFxBoard(b, allQuotes)
  // Rows of the base we asked for only: never show rates against a previous base under the new label.
  const boardRows = useMemo(() => (board.data?.base === b ? board.data.rows : []), [board.data, b])
  const rowByQuote = useMemo(() => new Map(boardRows.map((r) => [r.quote, r])), [boardRows])

  // Visible rows: the supported majors plus the currencies the user added – kept for this visit only.
  const [added, setAdded] = useState<string[]>([])
  const shown = useMemo(() => [...new Set([...converterCurrencies(base, supported), ...added])].filter((c) => c !== b && (supported ?? []).includes(c)),
    [base, supported, added, b])
  const addable = allQuotes.filter((c) => !shown.includes(c))

  const [pair, setPair] = useState<string>()
  const chartQuote = pair && pair !== b ? pair : shown[0]
  const chartHistorical = historical.find((d) => d.currency === chartQuote)
  const [range, setRange] = useState<(typeof ranges)[number]['label']>('1Y')
  const history = useFxHistory(b, chartQuote, ranges.find((r) => r.label === range)!.days)
  const points = history.data && history.data.from === b && history.data.to === chartQuote ? history.data.points : null
  const chartDates = useMemo(() => points?.map((p) => p.date) ?? [], [points])
  const chartSeries = useMemo(() => [{ name: `${chartQuote} per ${b}`, values: points?.map((p) => p.rate) ?? [] }], [points, chartQuote, b])
  const updating = history.isFetching && history.isPlaceholderData

  const [amount, setAmount] = useState('1000')
  const [from, setFrom] = useState<string>()
  const [to, setTo] = useState<string>()
  // Default pair: the chosen board base (or the first major) → the portfolio currency, never X → X.
  const fromCcy = from ?? (b !== base ? b : shown[0]) ?? base
  const toCcy = to ?? base
  // Cross rate via the board's base: every supported currency is on the board, so any pair converts without a request.
  const rates = useMemo(() => Object.fromEntries(boardRows.map((r) => [r.quote, r.rate])), [boardRows])
  const rate = boardRows.length ? crossFromBoard(rates, b, fromCcy, toCcy) : null
  const result = convertAmount(Number(amount.replace(',', '.')), rate)
  const pairRows = [rowByQuote.get(fromCcy), rowByQuote.get(toCcy)].filter((r) => r !== undefined)
  const rateDates = pairRows.map((r) => r.observationDate).filter((d): d is string => !!d)
  const asOf = rateDates.length ? rateDates.sort()[0] : boardRows.find((r) => r.observationDate)?.observationDate ?? null
  const missing = pairRows.filter((r) => r.problem)
  const boardLoading = !boardRows.length && (info.isLoading || board.isFetching)

  return (
    <Card className={className} role="region" aria-label="Currency converter">
      <CardHeader className="flex flex-row flex-wrap items-center justify-between gap-2">
        <CardTitle className="flex items-center gap-2 text-sm">
          <Coins className="size-4" aria-hidden /> Currency converter
          {/* The rate basis lives in the tip; the result line below still names the date of the rate used. */}
          <InfoTip label="About these rates">
            {board.data?.note ?? 'Reference rates, published once per business day.'} For analysis and reporting only – not a rate at which money can be
            exchanged; banks and brokers add spreads and fees.
            {historical.length > 0 && ` No longer published: ${historical.map((d) => `${d.currency} (until ${date(d.lastRateDate)}, chart history only)`).join(', ')}.`}
          </InfoTip>
        </CardTitle>
        {board.data?.isSimulated && <DataBadge kind="simulated" />}
      </CardHeader>
      <CardContent className="space-y-5">
        {(info.error || board.error) && <ErrorState error={info.error ?? board.error} onRetry={() => (info.error ? info.refetch() : board.refetch())} />}

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
          {boardLoading ? <span className="text-sm text-muted-foreground">Loading rates…</span> : result !== null ? (
            <>
              <div className="text-2xl font-semibold tabular" aria-label="Converted amount">
                {new Intl.NumberFormat('en-US', { style: 'currency', currency: toCcy, maximumFractionDigits: toCcy === 'JPY' ? 0 : 2 }).format(result)}
              </div>
              <div className="mt-0.5 text-xs text-muted-foreground">{fxPhrase(rate, fromCcy, toCcy)}{asOf ? ` · reference rate of ${date(asOf)}` : ''}</div>
            </>
          ) : (
            <span className="text-sm text-warn">
              {missing.length ? missing.map((m) => `${m.quote}: ${m.problem}`).join(' ') : board.error ? 'Rates could not be loaded.' : 'Enter a number to convert.'}
            </span>
          )}
        </div>

        {/* List and chart share one row on wide screens: the chart takes the list's height (list capped, then it scrolls). */}
        <div className="grid gap-4 lg:grid-cols-[minmax(0,15rem)_minmax(0,1fr)]">
          {/* Rate board */}
          <div className="flex min-w-0 flex-col">
            <div className="mb-2 flex items-center gap-2">
              <span className="text-xs font-medium text-muted-foreground uppercase">1</span>
              <CurrencySelect value={b} onChange={setBoardBase} supported={supported} first={base} label="Base currency of the rates" size="sm" className="w-24" />
              <span className="text-xs font-medium text-muted-foreground uppercase">=</span>
            </div>
            {boardLoading ? <LoadingBlock height="h-40" /> : (
              <ul className="max-h-96 space-y-1 overflow-y-auto overscroll-contain" aria-label={`Exchange rates against ${b}`}>
                {shown.map((q) => {
                  const r = rowByQuote.get(q)
                  return (
                    <li key={q} className="flex items-center gap-1">
                      <button type="button" onClick={() => setPair(q)} aria-pressed={q === chartQuote} title={r?.problem ?? currencyName(q)}
                        className={cn('grid min-w-0 flex-1 grid-cols-[3rem_1fr_4rem] items-center gap-2 rounded-md px-2 py-1 text-left text-sm hover:bg-muted/60',
                          q === chartQuote && 'bg-muted')}>
                        <span className="font-medium">{q}</span>
                        <span className="text-right tabular">{!r || r.rate === null ? '—' : r.rate.toLocaleString('en-US', { maximumFractionDigits: r.rate >= 100 ? 2 : 4 })}</span>
                        <span className="text-right text-xs tabular" title="Change since the previous published rate">
                          {!r || r.change1D === null ? '—' : <Signed value={r.change1D}>{pct(r.change1D, 2, true)}</Signed>}
                        </span>
                      </button>
                      {added.includes(q) && (
                        <Button variant="ghost" size="icon-xs" aria-label={`Remove ${q}`} onClick={() => setAdded(added.filter((c) => c !== q))}><X /></Button>
                      )}
                    </li>
                  )
                })}
              </ul>
            )}
            {/* The list is capped at 12 rows (max-h-96); say so instead of hiding rows silently. */}
            {!boardLoading && shown.length > 12 && <p className="mt-1 text-xs text-muted-foreground">{shown.length} currencies – scroll the list for more.</p>}
            {addable.length > 0 && (
              <CurrencySelect value={null} onChange={(c) => { setAdded((a) => (a.includes(c) ? a : [...a, c])); setPair(c) }} supported={addable}
                label="Add a currency to the rates" placeholder="+ Add currency" size="sm" className="mt-2 w-full" />
            )}
          </div>

          {/* Chart of the selected pair */}
          <div className="flex min-w-0 flex-col">
            <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
              <div className="flex items-center gap-2">
                {chartQuote && <CurrencySelect value={chartQuote} onChange={setPair} supported={supported} historical={historical.map((d) => d.currency)} first={base} exclude={b}
                  label="Currency shown in the chart" size="sm" className="w-24" />}
                <h3 className="text-xs font-medium text-muted-foreground uppercase">per {b}</h3>
                {updating && <span className="text-xs text-muted-foreground" role="status">Updating…</span>}
              </div>
              <div role="radiogroup" aria-label="Exchange-rate range" className="flex gap-1">
                {ranges.map((r) => (
                  <Button key={r.label} size="xs" variant={r.label === range ? 'secondary' : 'ghost'} role="radio" aria-checked={r.label === range} onClick={() => setRange(r.label)}>
                    {r.label}
                  </Button>
                ))}
              </div>
            </div>
            {/* Fixed height when stacked; beside the list it fills the row (min 15rem), so it follows the list's height. */}
            <div className={cn('h-60 min-h-60 lg:h-auto lg:flex-1', updating && 'opacity-60 transition-opacity')}>
              {history.isError && !points ? (
                <ErrorState error={history.error} onRetry={() => history.refetch()} />
              ) : history.isLoading || (!points && history.isFetching) ? <LoadingBlock height="h-full" /> : points && points.length > 1 ? (
                <TimeSeriesChart dates={chartDates} series={chartSeries} format={chartFormat} yDomain={['auto', 'auto']} height="100%" />
              ) : (
                <p className="text-sm text-muted-foreground">
                  {chartHistorical
                    ? `No ${chartQuote} rates in this period. ${chartHistorical.note} Choose 1Y or 5Y to see its history.`
                    : 'No published rates for this pair in the period.'}
                </p>
              )}
            </div>
            {points && points.length > 1 && !updating && (
              <p className="mt-1 text-xs text-muted-foreground">
                {range} change: <Signed value={points.at(-1)!.rate / points[0].rate - 1}>{pct(points.at(-1)!.rate / points[0].rate - 1, 2, true)}</Signed>{' '}
                ({points.length} published rates{chartHistorical ? `, until ${date(chartHistorical.lastRateDate)}` : ''})
              </p>
            )}
          </div>
        </div>
      </CardContent>
    </Card>
  )
}

const chartFormat = (v: number) => v.toLocaleString('en-US', { maximumFractionDigits: v >= 100 ? 1 : 4 })
