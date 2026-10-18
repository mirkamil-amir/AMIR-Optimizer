import { Link } from 'react-router'
import { Briefcase } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/common'

export function NoPortfolio() {
  return (
    <EmptyState
      icon={<Briefcase className="size-8" />}
      title="No portfolio yet"
      description="Create a simulated portfolio with Portfolio Creation: a name, a currency, starting cash and the stocks it may invest in."
      action={<Button render={<Link to="/stocks" />}>Create a portfolio</Button>}
    />
  )
}
