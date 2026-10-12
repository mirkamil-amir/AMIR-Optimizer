import { Link } from 'react-router'
import { Briefcase } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/common'

export function NoPortfolio() {
  return (
    <EmptyState
      icon={<Briefcase className="size-8" />}
      title="No portfolio yet"
      description="Create a simulated portfolio with an initial cash amount to start optimizing."
      action={<Button render={<Link to="/settings" />}>Create a portfolio</Button>}
    />
  )
}
