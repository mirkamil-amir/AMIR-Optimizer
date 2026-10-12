import { Link } from 'react-router'
import { Button } from '@/components/ui/button'
import { EmptyState } from '@/components/common'

export function NotFoundPage() {
  return <EmptyState title="Page not found" description="The page you requested does not exist." action={<Button render={<Link to="/" />}>Back to overview</Button>} />
}
