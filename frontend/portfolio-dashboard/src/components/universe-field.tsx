import { Link } from 'react-router'
import { X } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { useStocks } from '@/lib/queries'
import { useSelection } from '@/lib/selection'

/**
 * The stocks an optimization may invest in (Math and ML Optimization alike): the chosen stocks with name and a remove
 * button, "Browse stocks" to open the full stock list (`browseTo`, which starts with these stocks), the temporary selection
 * and Clear.
 * Stocks without enough stored prices are marked; the pages explain and offer the download.
 */
export function UniverseField({ symbols, onChange, browseTo }: { symbols: string[]; onChange: (symbols: string[]) => void; browseTo: string }) {
  const selection = useSelection()
  const stocks = useStocks({ symbols, pageSize: 200 }, symbols.length > 0)
  const bySymbol = new Map((stocks.data?.items ?? []).map((s) => [s.symbol, s]))
  const sameAsSelection = selection.count === symbols.length && selection.symbols.every((s) => symbols.includes(s))

  return (
    <fieldset className="space-y-2">
      <legend className="mb-1 flex w-full items-center justify-between text-sm font-medium">
        <span className="flex items-center gap-2">Equity universe <Badge variant="secondary" className="tabular">{symbols.length}</Badge></span>
        {symbols.length > 0 && (
          <Button type="button" size="xs" variant="ghost" onClick={() => onChange([])}>Clear</Button>
        )}
      </legend>

      {symbols.length === 0 ? (
        <p className="rounded-lg border border-dashed px-3 py-5 text-center text-sm text-muted-foreground">No stocks chosen yet.</p>
      ) : (
        <ul className="max-h-64 divide-y overflow-y-auto rounded-lg border" aria-label="Stocks in the universe">
          {symbols.map((symbol) => {
            const s = bySymbol.get(symbol)
            return (
              <li key={symbol} className="flex items-center gap-2 px-2.5 py-1.5 text-sm">
                <span className="w-16 shrink-0 font-medium">{symbol}</span>
                <span className="min-w-0 flex-1 truncate text-muted-foreground">{s?.name ?? ''}</span>
                {s && s.observations < 61 && <Badge variant="outline" className="text-[10px] text-warn">no history</Badge>}
                <Button type="button" variant="ghost" size="icon-xs" aria-label={`Remove ${symbol}`} onClick={() => onChange(symbols.filter((x) => x !== symbol))}><X /></Button>
              </li>
            )
          })}
        </ul>
      )}

      <div className="flex flex-wrap gap-1.5">
        <Button type="button" size="sm" variant="outline" render={<Link to={browseTo} state={{ universe: symbols }} />}>Browse stocks</Button>
        {selection.count > 0 && !sameAsSelection && (
          <Button type="button" size="sm" variant="ghost" onClick={() => onChange(selection.symbols)}>Use stock selection ({selection.count})</Button>
        )}
      </div>
    </fieldset>
  )
}
