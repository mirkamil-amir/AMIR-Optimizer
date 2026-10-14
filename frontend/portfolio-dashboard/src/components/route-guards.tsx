import { Suspense } from 'react'
import { Navigate, Outlet, useLocation } from 'react-router'
import { ShieldAlert } from 'lucide-react'
import { EmptyState, LoadingBlock } from '@/components/common'
import { useAuth } from '@/lib/auth'
import { PortfolioProvider } from '@/lib/portfolio-context'

/**
 * Renders the application only for signed-in, approved accounts. Unauthenticated visitors go to the sign-in page and
 * pending/rejected/suspended accounts to their status page. This is a usability layer; the API enforces access itself.
 */
export function RequireApprovedUser() {
  const { user, isLoading } = useAuth()
  const location = useLocation()
  if (isLoading) return <div className="p-8"><LoadingBlock height="h-[80svh]" /></div>
  if (!user) return <Navigate to="/login" replace state={{ from: location.pathname + location.search }} />
  if (user.status !== 'Approved') return <Navigate to="/account" replace />
  return (
    <PortfolioProvider>
      <Suspense fallback={<div className="p-8"><LoadingBlock height="h-[80svh]" /></div>}>
        <Outlet />
      </Suspense>
    </PortfolioProvider>
  )
}

export function RequireAdministrator() {
  const { isAdministrator } = useAuth()
  if (!isAdministrator) {
    return (
      <EmptyState
        icon={<ShieldAlert className="size-8" />}
        title="Administrators only"
        description="This area is restricted to administrators. The server rejects these requests for other accounts."
      />
    )
  }
  return <Outlet />
}
