import { useState } from 'react'
import { ChartCard } from '@/components/charts'
import { DataBadge, ErrorState, InfoTip } from '@/components/common'
import { Skeleton } from '@/components/ui/skeleton'
import { date } from '@/lib/format'
import { cellTitle, financialValue, filledPeriods, periodHeader, periodTitle, ROW_EXPLANATIONS, SECTION_LABELS } from '@/lib/financials'
import { useStockFinancials } from '@/lib/queries'
import type { FinancialSection, FinancialTable, StockFinancials } from '@/lib/types'
import { cn } from '@/lib/utils'

type PeriodType = FinancialTable['periodType']

/**
 * Accounting card of the stock-detail panel (AMIR-41): income statement, balance sheet, cash flow and ratios per fiscal
 * year or quarter (newest first), with the latest valuation figures below. Amounts are in the company's reporting
 * currency as published; ratios are computed from them. Independent of the price chart: a failure here never affects it.
 */
export function AccountingCard({ symbol, className }: { symbol: string; className?: string }) {
  const financials = useStockFinancials(symbol)
  // Only render figures that belong to the stock currently shown.
  const f = financials.data?.symbol === symbol ? financials.data : undefined
  const [periodType, setPeriodType] = useState<PeriodType>('FY')
  const [section, setSection] = useState<FinancialSection['key']>('income')

  const table = f ? (periodType === 'FY' ? f.annual : f.quarterly) ?? null : null
  const sections = table?.sections ?? []
  const shown = sections.find((s) => s.key === section) ?? sections[0]
  const currency = table?.sections.flatMap((s) => s.rows).find((r) => r.currency)?.currency ?? null

  return (
    <ChartCard
      className={className}
      title="Accounting"
      hint="Reported figures of the company as published by the source, in the currency it reports in (not converted). Ratios are computed from them. At most four fiscal years and five quarters come from the source; older periods appear as Matrix keeps them."
      description={f?.status === 'Available' ? (
        <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
          {currency && <span>Reported in {currency}</span>}
          {f.usesFinancialMeasures && <span>· bank and insurer measures</span>}
          {f.isSimulated && <DataBadge kind="simulated" />}
          {f.retrievedAtUtc && <span>· retrieved {date(f.retrievedAtUtc)}</span>}
        </span>
      ) : undefined}
    >
      {financials.error ? <ErrorState error={financials.error} onRetry={() => financials.refetch()} /> : !f ? (
        <div className="space-y-2" aria-busy="true" aria-label={`Loading the accounting figures of ${symbol}`}>
          <Skeleton className="h-7 w-2/3" />
          <Skeleton className="h-40 w-full" />
        </div>
      ) : f.status !== 'Available' ? (
        <Unavailable f={f} />
      ) : (
        <div className="space-y-3">
          <div className="flex flex-wrap items-center gap-2">
            <Segmented label="Periods" value={periodType} onChange={setPeriodType}
              options={[{ value: 'FY', label: 'Years', disabled: !f.annual }, { value: 'Q', label: 'Quarters', disabled: !f.quarterly }]} />
            {sections.length > 0 && (
              <Segmented label="Statement" value={shown?.key ?? section} onChange={setSection}
                options={sections.map((s) => ({ value: s.key, label: SECTION_LABELS[s.key] }))} />
            )}
          </div>
          {!table || !shown ? (
            <p className="text-sm text-muted-foreground">The source reports no {periodType === 'FY' ? 'fiscal-year' : 'quarterly'} figures for {symbol}.</p>
          ) : (
            <StatementTable table={table} section={shown} symbol={symbol} />
          )}
          {f.message && <p className="text-xs text-warn">{f.message}</p>}
          {f.valuation.length > 0 && <Valuation f={f} />}
        </div>
      )}
    </ChartCard>
  )
}

function StatementTable({ table, section, symbol }: { table: FinancialTable; section: FinancialSection; symbol: string }) {
  // Periods for which this statement has no value at all (another statement reaches further back) are left out.
  const shown = filledPeriods(section)
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <caption className="sr-only">{`${section.title} of ${symbol}, ${table.periodType === 'FY' ? 'fiscal years' : 'quarters'}, newest first`}</caption>
        <thead>
          <tr className="border-b text-xs text-muted-foreground">
            <th scope="col" className="sticky left-0 bg-card py-1.5 pr-3 text-left font-medium">{section.title}</th>
            {shown.map((i) => table.periodEnds[i]).map((end) => (
              <th key={end} scope="col" title={periodTitle(end, table.periodType)} className="py-1.5 pl-3 text-right font-medium tabular whitespace-nowrap">
                {periodHeader(end, table.periodType)}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {section.rows.map((row) => (
            <tr key={row.key} className="border-b border-border/50 last:border-0">
              <th scope="row" className="sticky left-0 bg-card py-1.5 pr-3 text-left font-normal">
                {ROW_EXPLANATIONS[row.key] ? <InfoTip of={row.label}>{ROW_EXPLANATIONS[row.key]}</InfoTip> : row.label}
              </th>
              {shown.map((i) => (
                <td key={table.periodEnds[i]} title={cellTitle(row, i)}
                  className={cn('py-1.5 pl-3 text-right tabular whitespace-nowrap', row.values[i] === null && 'text-muted-foreground')}>
                  {financialValue(row.kind, row.values[i], row.currency)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}

function Valuation({ f }: { f: StockFinancials }) {
  return (
    <section aria-label="Valuation" className="border-t pt-3">
      <dl className="grid grid-cols-2 gap-x-4 gap-y-2 text-sm sm:grid-cols-4">
        {f.valuation.map((v) => (
          <div key={v.key} title={v.asOf ? `As of ${date(v.asOf)}` : undefined}>
            <dt className="text-xs text-muted-foreground">
              {ROW_EXPLANATIONS[v.key] ? <InfoTip of={v.label}>{ROW_EXPLANATIONS[v.key]}</InfoTip> : v.label}
            </dt>
            <dd className="tabular">{financialValue(v.kind, v.value, v.currency)}</dd>
          </div>
        ))}
      </dl>
      <p className="mt-2 text-xs text-muted-foreground">
        Valuation as published by the source{f.valuation[0]?.asOf ? `, latest as of ${date(f.valuation.map((v) => v.asOf ?? '').sort().at(-1))}` : ''}; it can lag the latest price.
      </p>
    </section>
  )
}

function Unavailable({ f }: { f: StockFinancials }) {
  if (f.status === 'Pending')
    return <p className="text-sm text-muted-foreground" role="status">Company figures are being retrieved…</p>
  return (
    <div className={cn('rounded-md border border-dashed p-3 text-sm', f.status === 'Error' && 'border-warn/50')} role="status">
      <p className="font-medium">
        {f.status === 'NotApplicable' ? 'No accounting figures for ETFs and indices.' : 'Accounting figures are unavailable.'}
      </p>
      {f.message && f.status !== 'NotApplicable' && <p className="text-xs text-muted-foreground">{f.message}</p>}
    </div>
  )
}

function Segmented<T extends string>({ label, value, onChange, options }: {
  label: string
  value: T
  onChange: (v: T) => void
  options: { value: T; label: string; disabled?: boolean }[]
}) {
  return (
    <div role="radiogroup" aria-label={label} className="inline-flex rounded-md border p-0.5">
      {options.map((o) => (
        <button key={o.value} type="button" role="radio" aria-checked={value === o.value} disabled={o.disabled}
          onClick={() => onChange(o.value)}
          className={cn('rounded px-2.5 py-1 text-xs font-medium text-muted-foreground transition-colors hover:text-foreground disabled:opacity-40',
            value === o.value && 'bg-muted text-foreground')}>
          {o.label}
        </button>
      ))}
    </div>
  )
}
