import type { DataCheck } from '@/lib/types'

/** Missing prices and exchange rates of a data check, with the reason for each. */
export function MissingDataList({ check }: { check: DataCheck }) {
  return (
    <div className="max-h-72 space-y-3 overflow-y-auto text-xs">
      {check.prices.length > 0 && (
        <div>
          <div className="mb-1 font-medium">Prices ({check.prices.length})</div>
          <ul className="space-y-1">{check.prices.map((p) => (
            <li key={p.symbol}><span className="font-medium">{p.symbol}</span> <span className="text-muted-foreground">{p.explanation}</span></li>
          ))}</ul>
        </div>
      )}
      {check.rates.length > 0 && (
        <div>
          <div className="mb-1 font-medium">Exchange rates ({check.rates.length})</div>
          <ul className="space-y-1">{check.rates.map((r) => (
            <li key={r.currency}><span className="font-medium">{r.currency}</span> <span className="text-muted-foreground">{r.explanation}</span></li>
          ))}</ul>
        </div>
      )}
      {check.notes.map((n, i) => <p key={i} className="text-muted-foreground">{n}</p>)}
      {check.complete && <p className="text-muted-foreground">Everything needed is stored.</p>}
    </div>
  )
}
