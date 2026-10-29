import { Link } from 'react-router'
import { X } from 'lucide-react'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { SearchableTable, type Column } from '@/components/data-table'
import { useStocksBySymbols } from '@/lib/queries'
import { useSelection } from '@/lib/selection'

/** Fewer stored prices than this: the stock cannot enter an optimization yet (the pages explain and offer the download). */
const MIN_OBSERVATIONS = 61

interface UniverseRow { symbol: string; name: string; observations: number | null }

/**
 * The stocks an optimization may invest in (Math and ML Optimization alike): the chosen stocks as a searchable, paged
 * list with name and a remove button, "Browse stocks" to open the full stock list (`browseTo`, which starts with these
 * stocks), the temporary selection and Clear.
 * Stocks without enough stored prices are marked; the pages explain and offer the download.
 */
export function UniverseField({ symbols, onChange, browseTo }: { symbols: string[]; onChange: (symbols: string[]) => void; browseTo: string }) {
  const selection = useSelection()
  const stocks = useStocksBySymbols(symbols)
  const bySymbol = new Map(stocks.items.map((s) => [s.symbol, s]))
  const sameAsSelection = selection.count === symbols.length && selection.symbols.every((s) => symbols.includes(s))
  const rows: UniverseRow[] = symbols.map((symbol) => {
    const s = bySymbol.get(symbol)
    return { symbol, name: s?.name ?? '', observations: s ? s.observations : null }
  })
  const short = rows.filter((r) => r.observations !== null && r.observations < MIN_OBSERVATIONS).length

  const columns: Column<UniverseRow>[] = [
    { key: 'symbol', header: 'Symbol', sortValue: (r) => r.symbol, cell: (r) => <span className="font-medium">{r.symbol}</span> },
    { key: 'name', header: 'Name', sortValue: (r) => r.name, cell: (r) => <span className="block max-w-56 truncate text-muted-foreground">{r.name}</span> },
    {
      key: 'history', header: 'History', align: 'right', sortValue: (r) => r.observations,
      cell: (r) => r.observations === null ? <span className="text-xs text-muted-foreground">—</span>
        : r.observations < MIN_OBSERVATIONS ? <Badge variant="outline" className="text-[10px] text-warn">no history</Badge>
        : <span className="text-xs text-muted-foreground">{r.observations.toLocaleString()} days</span>,
    },
    {
      key: 'remove', header: <span className="sr-only">Remove</span>, align: 'right',
      cell: (r) => <Button type="button" variant="ghost" size="icon-xs" aria-label={`Remove ${r.symbol}`} onClick={() => onChange(symbols.filter((x) => x !== r.symbol))}><X /></Button>,
    },
  ]

  return (
    // min-w-0: a fieldset is as wide as its content by default; the table scrolls inside it instead of widening the page.
    <fieldset className="min-w-0 space-y-2">
      <legend className="mb-1 flex w-full items-center justify-between text-sm font-medium">
        <span className="flex items-center gap-2">Equity universe <Badge variant="secondary" className="tabular">{symbols.length}</Badge></span>
        {symbols.length > 0 && (
          <Button type="button" size="xs" variant="ghost" onClick={() => onChange([])}>Clear</Button>
        )}
      </legend>

      {symbols.length === 0 ? (
        <p className="rounded-lg border border-dashed px-3 py-5 text-center text-sm text-muted-foreground">No stocks chosen yet.</p>
      ) : (
        <SearchableTable label="stocks in the universe" caption="Stocks in the universe" rows={rows} columns={columns} rowKey={(r) => r.symbol} dense pageSize={25} maxRows={8}
          placeholder="Search symbol or name"
          matches={(r, q) => r.symbol.toLowerCase().includes(q) || r.name.toLowerCase().includes(q)}
          breakdown={`${symbols.length.toLocaleString()} stocks${stocks.isLoading ? '' : ` · ${short.toLocaleString()} without enough history`}`} />
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
