import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'

export interface SelectedStock {
  symbol: string
  name: string | null
  sector: string | null
  /** Trading currency; analyses need a single-currency selection (no FX conversion). */
  currency?: string
}

interface SelectionContextValue {
  stocks: SelectedStock[]
  symbols: string[]
  count: number
  /** Distinct currencies in the selection (known ones). */
  currencies: string[]
  has: (symbol: string) => boolean
  add: (stock: SelectedStock) => void
  addMany: (stocks: SelectedStock[]) => void
  remove: (symbol: string) => void
  toggle: (stock: SelectedStock) => void
  clear: () => void
}

const SelectionContext = createContext<SelectionContextValue | null>(null)

/**
 * The temporary stock selection. It lives only in memory: it is never written to browser storage or to the server, and
 * it survives navigation between sectors and pages but not a reload. It is submitted with an optimization run, ML
 * experiment or comparison request, which is when the server stores the universe as part of that run.
 */
export function SelectionProvider({ children }: { children: ReactNode }) {
  const [items, setItems] = useState<Map<string, SelectedStock>>(() => new Map())

  const add = useCallback((s: SelectedStock) => setItems((m) => (m.has(s.symbol) ? m : new Map(m).set(s.symbol, s))), [])
  const addMany = useCallback((list: SelectedStock[]) => setItems((m) => {
    const next = new Map(m)
    for (const s of list) if (!next.has(s.symbol)) next.set(s.symbol, s)
    return next
  }), [])
  const remove = useCallback((symbol: string) => setItems((m) => {
    if (!m.has(symbol)) return m
    const next = new Map(m)
    next.delete(symbol)
    return next
  }), [])
  const toggle = useCallback((s: SelectedStock) => setItems((m) => {
    const next = new Map(m)
    if (next.has(s.symbol)) next.delete(s.symbol)
    else next.set(s.symbol, s)
    return next
  }), [])
  const clear = useCallback(() => setItems(new Map()), [])

  const value = useMemo<SelectionContextValue>(() => {
    const stocks = [...items.values()].sort((a, b) => a.symbol.localeCompare(b.symbol))
    return {
      stocks,
      symbols: stocks.map((s) => s.symbol),
      count: stocks.length,
      currencies: [...new Set(stocks.map((s) => s.currency).filter((c): c is string => !!c))].sort(),
      has: (symbol) => items.has(symbol),
      add,
      addMany,
      remove,
      toggle,
      clear,
    }
  }, [items, add, addMany, remove, toggle, clear])

  return <SelectionContext.Provider value={value}>{children}</SelectionContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useSelection() {
  const ctx = useContext(SelectionContext)
  if (!ctx) throw new Error('useSelection must be used inside SelectionProvider')
  return ctx
}
