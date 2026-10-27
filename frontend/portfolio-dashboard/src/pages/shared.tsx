import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState, MetricCard } from '@/components/common'
import { date, money } from '@/lib/format'
import { useHoldings } from '@/lib/queries'

export function NoPortfolio() {
  return (
    <EmptyState
      title="No portfolio yet"
      description="Create a simulated portfolio in Forge: a name, a currency, starting cash and the stocks it may invest in."
      action={<Button render={<Link to="/stocks" />}>Forge a portfolio</Button>}
    />
  )
}

/**
 * The money an optimization of this portfolio would invest: its total value today (cash + positions at the latest stored
 * prices). It is the current value – a stored run's weights are applied to it when a trade proposal is made.
 */
export function InvestableValueCard({ portfolioId }: { portfolioId: string | null | undefined }) {
  const holdings = useHoldings(portfolioId ?? undefined)
  const s = holdings.data?.summary
  return (
    <MetricCard label="Investable value" loading={!!portfolioId && !s}
      value={!portfolioId ? '—' : s?.totalValue == null ? 'Not available' : money(s.totalValue, s.currency)}
      sub={s?.valuationDate ? `Today · prices of ${date(s.valuationDate)}` : 'Today · cash only'}
      hint="Total value of the portfolio today (cash + positions at the latest stored prices). To invest a different amount, deposit or withdraw cash, or create another portfolio." />
  )
}
