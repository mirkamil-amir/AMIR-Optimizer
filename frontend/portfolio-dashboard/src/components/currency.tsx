import { AlertTriangle, Coins } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { exposureRows, fxPhrase } from '@/lib/currency'
import { date, pct } from '@/lib/format'
import { useFxInfo } from '@/lib/queries'
import type { CurrencyExposure } from '@/lib/types'

/** Rate used for a value, with its date: "1 USD = 0.9132 EUR · 15 Oct 2026". */
export function FxNote({ rate, from, to, on }: { rate: number | null | undefined; from: string; to: string; on?: string | null }) {
  if (from === to || rate === null || rate === undefined) return null
  return (
    <span className="text-xs text-muted-foreground" title="Reference rate used for conversion (units of the target per one unit of the source). Not an exchange of money.">
      {fxPhrase(rate, from, to)}{on ? ` · ${date(on)}` : ''}
    </span>
  )
}

/** Choose the currency values are shown in; the portfolio itself is unchanged. */
export function ReportingCurrencySelect({ value, accountCurrency, onChange }: { value: string | undefined; accountCurrency: string; onChange: (c: string | undefined) => void }) {
  const fx = useFxInfo()
  const options = [accountCurrency, ...(fx.data?.currencies ?? []).filter((c) => c !== accountCurrency)]
  return (
    <Select value={value ?? accountCurrency} onValueChange={(v) => onChange(v === accountCurrency ? undefined : (v as string))}
      items={options.map((c) => ({ value: c, label: c === accountCurrency ? `${c} (portfolio)` : c }))}>
      <SelectTrigger className="w-40" aria-label="Show values in currency"><SelectValue /></SelectTrigger>
      <SelectContent>{options.map((c) => <SelectItem key={c} value={c}>{c === accountCurrency ? `${c} (portfolio)` : c}</SelectItem>)}</SelectContent>
    </Select>
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
      <AlertTriangle className="text-warn" />
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
      <Coins />
      <AlertTitle>Shown in {currency} – a reporting conversion</AlertTitle>
      <AlertDescription>
        The portfolio holds its cash in {accountCurrency}. Positions are converted at the rate of their price date; cost, fees and realized
        results (booked in {accountCurrency}) at today&apos;s rate {rate ? `(${fxPhrase(rate, accountCurrency, currency)})` : '(unavailable)'}. Returns are measured in {accountCurrency}.
        Nothing is exchanged and the holdings do not change.
      </AlertDescription>
    </Alert>
  )
}
