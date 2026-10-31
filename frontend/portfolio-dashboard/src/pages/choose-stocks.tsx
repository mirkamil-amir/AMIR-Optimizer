import { useEffect, useRef } from 'react'
import { Link, useLocation, useNavigate } from 'react-router'
import { Button } from '@/components/ui/button'
import { PageHeader } from '@/components/common'
import { StockChooser } from '@/components/stock-chooser'
import { portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { useStocks } from '@/lib/queries'
import { useSelection } from '@/lib/selection'
import { NoPortfolio } from './shared'

const labels = { optimize: 'Math Optimization', 'ml-optimize': 'ML Optimization' } as const

/**
 * Choose the stocks of a Math or ML Optimization (`/portfolios/:id/optimize/stocks`, `…/ml-optimize/stocks`): the stock
 * list of New portfolio without the creation form. It starts with the optimization's current stocks (passed in the
 * navigation state), works on the temporary selection, and "Use" returns to the optimization with it as the universe.
 */
export function ChooseStocksPage({ section }: { section: keyof typeof labels }) {
  const { portfolioId, portfolio, isLoading } = usePortfolioContext()
  const navigate = useNavigate()
  const location = useLocation()
  const selection = useSelection()
  const initial = (location.state as { universe?: string[] } | null)?.universe
  const initialStocks = useStocks({ symbols: initial, pageSize: 200 }, !!initial?.length)

  // Start once from the optimization's stocks (names and currencies from the catalogue).
  const seeded = useRef(false)
  const { clear, addMany } = selection
  useEffect(() => {
    if (seeded.current || !initial) return
    if (initial.length > 0 && !initialStocks.data) return
    seeded.current = true
    clear()
    addMany((initialStocks.data?.items ?? []).map((s) => ({ symbol: s.symbol, name: s.name, sector: s.sector, currency: s.currency })))
  }, [initial, initialStocks.data, clear, addMany])

  if (!isLoading && !portfolioId) return <NoPortfolio />
  const id = portfolioId ?? ''
  const back = portfolioPath(id, section)
  const use = (
    <Button size="sm" disabled={selection.count === 0} onClick={() => navigate(`${back}?from=selection`)}>
      Use {selection.count} stock{selection.count === 1 ? '' : 's'}
    </Button>
  )

  return (
    <>
      <PageHeader
        level={2}
        title="Choose stocks"
        info={`Pick the stocks the ${labels[section]} of “${portfolio?.name ?? 'this portfolio'}” may invest in, then use them – they replace its equity universe. Nothing is saved until you run the optimization.`}
        actions={<Button variant="outline" size="sm" render={<Link to={back} />}>Back to {labels[section]}</Button>}
      />
      <StockChooser trayActions={use} priceCurrency={portfolio?.baseCurrency} />
    </>
  )
}
