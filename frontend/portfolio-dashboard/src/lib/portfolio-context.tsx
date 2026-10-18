import { createContext, useContext, useMemo, useState, type ReactNode } from 'react'
import { usePortfolios } from './queries'
import type { PortfolioSummary } from './types'

interface PortfolioContextValue {
  /**
   * The portfolio being worked on: inside a portfolio workspace (`/portfolios/:id/…`) always the one in the URL; elsewhere
   * the most recently opened active portfolio (used only as a default, e.g. for backtest settings).
   */
  portfolioId?: string
  portfolio?: PortfolioSummary
  /** All of the user's portfolios (active and archived), as returned by the server for the signed-in user. */
  portfolios: PortfolioSummary[]
  isLoading: boolean
  /** True inside a portfolio workspace route, where the URL – not browser memory – decides the portfolio. */
  scoped: boolean
  /** Remembers a portfolio as the most recently opened one. */
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

/** Loads the signed-in user's portfolios and remembers the most recently opened one (per browser). */
export function PortfolioProvider({ children }: { children: ReactNode }) {
  const { data, isLoading } = usePortfolios()
  const [selected, setSelected] = useState<string | undefined>(readStored)
  const portfolios = useMemo(() => data ?? [], [data])

  // Fall back to the first active portfolio when the remembered one no longer exists or was archived (derived, no effect needed).
  const active = portfolios.filter((p) => p.status !== 'Archived')
  const effectiveId = active.some((p) => p.id === selected) ? selected : active[0]?.id

  const value = useMemo<PortfolioContextValue>(() => ({
    portfolioId: effectiveId,
    portfolio: portfolios.find((p) => p.id === effectiveId),
    portfolios,
    isLoading,
    scoped: false,
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

/** Makes `portfolioId` the context of everything below it (used by the portfolio workspace route). */
export function PortfolioScope({ portfolioId, children }: { portfolioId: string; children: ReactNode }) {
  const parent = usePortfolioContext()
  const value = useMemo<PortfolioContextValue>(() => ({
    ...parent,
    portfolioId,
    portfolio: parent.portfolios.find((p) => p.id === portfolioId),
    scoped: true,
  }), [parent, portfolioId])
  return <PortfolioContext.Provider value={value}>{children}</PortfolioContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function usePortfolioContext() {
  const ctx = useContext(PortfolioContext)
  if (!ctx) throw new Error('usePortfolioContext must be used inside PortfolioProvider')
  return ctx
}

/** Path of a portfolio workspace section, e.g. `portfolioPath(id, 'optimize')`. */
// eslint-disable-next-line react-refresh/only-export-components
export function portfolioPath(id: string, section?: string) {
  return section ? `/portfolios/${id}/${section}` : `/portfolios/${id}`
}
