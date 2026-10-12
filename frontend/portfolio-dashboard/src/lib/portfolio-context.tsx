import { createContext, useContext, useMemo, useState, type ReactNode } from 'react'
import { usePortfolios } from './queries'
import type { PortfolioSummary } from './types'

interface PortfolioContextValue {
  portfolioId?: string
  portfolio?: PortfolioSummary
  portfolios: PortfolioSummary[]
  isLoading: boolean
  select: (id: string) => void
}

const PortfolioContext = createContext<PortfolioContextValue | null>(null)
const STORAGE_KEY = 'po.selectedPortfolio'

function readStored(): string | undefined {
  try {
    return localStorage.getItem(STORAGE_KEY) ?? undefined
  } catch {
    return undefined
  }
}

/** Holds the currently selected portfolio (remembered per browser). */
export function PortfolioProvider({ children }: { children: ReactNode }) {
  const { data, isLoading } = usePortfolios()
  const [selected, setSelected] = useState<string | undefined>(readStored)
  const portfolios = useMemo(() => data ?? [], [data])

  // Fall back to the first portfolio when the remembered one no longer exists (derived, no effect needed).
  const effectiveId = portfolios.some((p) => p.id === selected) ? selected : portfolios[0]?.id

  const value = useMemo<PortfolioContextValue>(() => ({
    portfolioId: effectiveId,
    portfolio: portfolios.find((p) => p.id === effectiveId),
    portfolios,
    isLoading,
    select: (id) => {
      setSelected(id)
      try {
        localStorage.setItem(STORAGE_KEY, id)
      } catch {
        /* storage unavailable */
      }
    },
  }), [portfolios, effectiveId, isLoading])

  return <PortfolioContext.Provider value={value}>{children}</PortfolioContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function usePortfolioContext() {
  const ctx = useContext(PortfolioContext)
  if (!ctx) throw new Error('usePortfolioContext must be used inside PortfolioProvider')
  return ctx
}
