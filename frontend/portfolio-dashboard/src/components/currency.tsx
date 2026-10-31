import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectSeparator, SelectTrigger, SelectValue } from '@/components/ui/select'
import { convertedPrice, currencyGroups, currencyName, exposureRows, fxPhrase } from '@/lib/currency'
import { date, money, pct } from '@/lib/format'
import { cn } from '@/lib/utils'
import { useFxInfo } from '@/lib/queries'
import type { CurrencyExposure } from '@/lib/types'

/**
 * Price column of position lists (trade proposal, holdings): the share price in `to`, with only the rate underneath as
 * "1 <to> = x <from>" (its date on hover). Same currency: the price alone.
 */
export function ConvertedPriceCell({ price, from, to, rate, rateDate }: { price: number | null; from: string; to: string; rate: number | null; rateDate: string | null }) {
  return (
    <div>
      <div>{money(convertedPrice(price, from, to, rate), to)}</div>
      {from && from !== to && rate !== null && rate > 0 && (
        <div className="text-xs whitespace-nowrap text-muted-foreground" title={`Reference rate${rateDate ? ` of ${date(rateDate)}` : ''} used for the conversion. Not an exchange of money.`}>
          {fxPhrase(1 / rate, to, from)}
        </div>
      )}
    </div>
  )
}

/** "Original price" column: the price in the stock's own currency with its close date (and "stale" when old). */
export function OriginalPriceCell({ price, currency, priceDate, stale }: { price: number | null; currency: string; priceDate: string | null; stale: boolean }) {
  return (
    <div>
      <div>{money(price, currency)}</div>
      <div className={cn('text-xs whitespace-nowrap', stale ? 'text-warn' : 'text-muted-foreground')}>{priceDate ? date(priceDate) : ''}{stale ? ' · stale' : ''}</div>
    </div>
  )
}

/** Rate used for a value, with its date: "1 USD = 0.9132 EUR · 15 Oct 2026". */
export function FxNote({ rate, from, to, on }: { rate: number | null | undefined; from: string; to: string; on?: string | null }) {
  if (from === to || rate === null || rate === undefined) return null
  return (
    <span className="text-xs text-muted-foreground" title="Reference rate used for conversion (units of the target per one unit of the source). Not an exchange of money.">
      {fxPhrase(rate, from, to)}{on ? ` · ${date(on)}` : ''}
    </span>
  )
}

/**
 * Currency drop-down over every supported currency: `first` and the majors, then all others, each with its English name.
 * The trigger shows the code only so it stays narrow; `firstNote` marks `first` in both ("EUR (portfolio)").
 */
export function CurrencySelect({ value, onChange, supported, historical, first, firstNote, exclude, label, id, size, className, placeholder }: {
  value: string | null; onChange: (c: string) => void; supported: string[] | undefined
  /** Discontinued currencies (history only, e.g. BGN), listed last in their own group. */
  historical?: string[]
  first?: string; firstNote?: string; exclude?: string
  label: string; id?: string; size?: 'sm' | 'default'; className?: string; placeholder?: string
}) {
  const { major, more } = currencyGroups(supported, first, exclude)
  const note = (c: string) => (firstNote && c === first ? ` (${firstNote})` : '')
  const past = (historical ?? []).filter((c) => c !== exclude && !major.includes(c) && !more.includes(c))
  const items = [...major, ...more, ...past].map((c) => ({ value: c, label: c + note(c) }))
  const option = (c: string) => (
    <SelectItem key={c} value={c}>
      <span className="w-9 font-medium">{c}</span><span className="text-muted-foreground">{currencyName(c)}{note(c)}</span>
    </SelectItem>
  )
  return (
    <Select value={value} onValueChange={(v) => v && onChange(String(v))} items={items}>
      <SelectTrigger id={id} size={size} className={className} aria-label={label}><SelectValue placeholder={placeholder} /></SelectTrigger>
      {/* A plain drop-down (flips above when there is no room below): aligning the chosen item with the trigger pushed long
          lists off-screen near the bottom of the page. */}
      <SelectContent className="max-h-80 min-w-56" alignItemWithTrigger={false}>
        {major.length > 0 && <SelectGroup><SelectLabel>Major currencies</SelectLabel>{major.map(option)}</SelectGroup>}
        {more.length > 0 && <>{major.length > 0 && <SelectSeparator />}<SelectGroup><SelectLabel>More currencies</SelectLabel>{more.map(option)}</SelectGroup></>}
        {past.length > 0 && <><SelectSeparator /><SelectGroup><SelectLabel>History only (no current rate)</SelectLabel>{past.map(option)}</SelectGroup></>}
      </SelectContent>
    </Select>
  )
}

/** Choose the currency values are shown in; the portfolio itself is unchanged. */
export function ReportingCurrencySelect({ value, accountCurrency, onChange }: { value: string | undefined; accountCurrency: string; onChange: (c: string | undefined) => void }) {
  const fx = useFxInfo()
  return (
    <CurrencySelect value={value ?? accountCurrency} onChange={(c) => onChange(c === accountCurrency ? undefined : c)} supported={fx.data?.currencies}
      first={accountCurrency} firstNote="portfolio" label="Show values in currency" className="w-40" />
  )
}

/** Share of value by trading currency (unhedged exposure). */
export function CurrencyExposureList({ items }: { items: CurrencyExposure[] | null | undefined }) {
  const rows = exposureRows(items)
  if (rows.length === 0) return <p className="text-sm text-muted-foreground">No currency exposure to show.</p>
  return (
    <ul className="space-y-1.5" aria-label="Currency exposure">
      {rows.map((e) => (
        <li key={e.currency} className="grid grid-cols-[5.5rem_1fr_3.5rem] items-center gap-2 text-sm">
          <span className="font-medium">{e.currency}</span>
          <div className="h-2 rounded bg-muted" aria-hidden><div className="h-2 rounded bg-series-3" style={{ width: `${Math.max(1, e.weight * 100)}%` }} /></div>
          <span className="text-right tabular">{pct(e.weight, 1)}</span>
        </li>
      ))}
    </ul>
  )
}

/** Values cannot be totalled because an exchange rate is missing: say so instead of showing a partial number. */
export function ValuationProblems({ problems }: { problems: string[] | null | undefined }) {
  if (!problems?.length) return null
  return (
    <Alert className="border-warn/50">
      <AlertTitle>Some positions cannot be valued</AlertTitle>
      <AlertDescription>
        <ul className="list-disc space-y-0.5 pl-4">{problems.map((p) => <li key={p}>{p}</li>)}</ul>
        <p className="mt-1">Totals are not shown until a usable exchange rate is available – nothing is converted with a guessed rate.</p>
      </AlertDescription>
    </Alert>
  )
}

/** Explains a reporting view in another currency than the portfolio's. */
export function ReportingNote({ currency, accountCurrency, rate }: { currency: string; accountCurrency: string; rate: number | null | undefined }) {
  if (currency === accountCurrency) return null
  return (
    <Alert>
      <AlertTitle>Shown in {currency} – a reporting conversion</AlertTitle>
      <AlertDescription>
        The portfolio holds its cash in {accountCurrency}. Positions are converted at the rate of their price date; cost, fees and realized
        results (booked in {accountCurrency}) at today&apos;s rate {rate ? `(${fxPhrase(rate, accountCurrency, currency)})` : '(unavailable)'}. Returns are measured in {accountCurrency}.
        Nothing is exchanged and the holdings do not change.
      </AlertDescription>
    </Alert>
  )
}
