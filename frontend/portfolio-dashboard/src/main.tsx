import { StrictMode, lazy } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { AppShell } from '@/components/app-shell'
import { PortfolioProvider } from '@/lib/portfolio-context'
import { ApiError } from '@/lib/api'
import './index.css'

// Route-level code splitting keeps the initial bundle small.
const OverviewPage = lazy(() => import('@/pages/overview').then((m) => ({ default: m.OverviewPage })))
const HoldingsPage = lazy(() => import('@/pages/holdings').then((m) => ({ default: m.HoldingsPage })))
const OptimizePage = lazy(() => import('@/pages/optimize').then((m) => ({ default: m.OptimizePage })))
const TradesPage = lazy(() => import('@/pages/trades').then((m) => ({ default: m.TradesPage })))
const AnalyticsPage = lazy(() => import('@/pages/analytics').then((m) => ({ default: m.AnalyticsPage })))
const BacktestPage = lazy(() => import('@/pages/backtest').then((m) => ({ default: m.BacktestPage })))
const MarketDataPage = lazy(() => import('@/pages/market-data').then((m) => ({ default: m.MarketDataPage })))
const SettingsPage = lazy(() => import('@/pages/settings').then((m) => ({ default: m.SettingsPage })))
const NotFoundPage = lazy(() => import('@/pages/not-found').then((m) => ({ default: m.NotFoundPage })))

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      staleTime: 30_000,
      refetchOnWindowFocus: false,
      retry: (count, error) => !(error instanceof ApiError && error.status >= 400 && error.status < 500) && count < 2,
    },
  },
})

const router = createBrowserRouter([
  {
    element: <AppShell />,
    children: [
      { path: '/', element: <OverviewPage /> },
      { path: '/holdings', element: <HoldingsPage /> },
      { path: '/optimize', element: <OptimizePage /> },
      { path: '/optimize/:runId', element: <OptimizePage /> },
      { path: '/trades', element: <TradesPage /> },
      { path: '/analytics', element: <AnalyticsPage /> },
      { path: '/backtest', element: <BacktestPage /> },
      { path: '/backtest/:backtestId', element: <BacktestPage /> },
      { path: '/market-data', element: <MarketDataPage /> },
      { path: '/settings', element: <SettingsPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
])

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <TooltipProvider delay={200}>
        <PortfolioProvider>
          <RouterProvider router={router} />
        </PortfolioProvider>
      </TooltipProvider>
      <Toaster richColors position="bottom-right" />
    </QueryClientProvider>
  </StrictMode>,
)
