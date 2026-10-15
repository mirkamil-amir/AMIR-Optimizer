import { StrictMode, lazy } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { createBrowserRouter, RouterProvider } from 'react-router'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { AppShell } from '@/components/app-shell'
import { RequireAdministrator, RequireApprovedUser } from '@/components/route-guards'
import { ApiError } from '@/lib/api'
import { AuthProvider } from '@/lib/auth'
import { SelectionProvider } from '@/lib/selection'
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
const StocksPage = lazy(() => import('@/pages/stocks').then((m) => ({ default: m.StocksPage })))
const StockPage = lazy(() => import('@/pages/stock').then((m) => ({ default: m.StockPage })))
const WatchlistPage = lazy(() => import('@/pages/watchlist').then((m) => ({ default: m.WatchlistPage })))
const NewsPage = lazy(() => import('@/pages/news').then((m) => ({ default: m.NewsPage })))
const RealityCheckPage = lazy(() => import('@/pages/reality-check').then((m) => ({ default: m.RealityCheckPage })))
const HistoryPage = lazy(() => import('@/pages/history').then((m) => ({ default: m.HistoryPage })))
const MachineLearningPage = lazy(() => import('@/pages/machine-learning').then((m) => ({ default: m.MachineLearningPage })))
const StrategyComparisonPage = lazy(() => import('@/pages/strategy-comparison').then((m) => ({ default: m.StrategyComparisonPage })))
const AdminUsersPage = lazy(() => import('@/pages/admin').then((m) => ({ default: m.AdminUsersPage })))
const AdminSettingsPage = lazy(() => import('@/pages/admin').then((m) => ({ default: m.AdminSettingsPage })))
const AdminOverviewPage = lazy(() => import('@/pages/admin-overview').then((m) => ({ default: m.AdminOverviewPage })))
const LearnIndexPage = lazy(() => import('@/pages/learn').then((m) => ({ default: m.LearnIndexPage })))
const StrategyGuidePage = lazy(() => import('@/pages/learn').then((m) => ({ default: m.StrategyGuidePage })))
const GlossaryPage = lazy(() => import('@/pages/learn').then((m) => ({ default: m.GlossaryPage })))
const LoginPage = lazy(() => import('@/pages/auth').then((m) => ({ default: m.LoginPage })))
const RegisterPage = lazy(() => import('@/pages/auth').then((m) => ({ default: m.RegisterPage })))
const AccountStatusPage = lazy(() => import('@/pages/auth').then((m) => ({ default: m.AccountStatusPage })))
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
  { path: '/login', element: <LoginPage /> },
  { path: '/register', element: <RegisterPage /> },
  { path: '/account', element: <AccountStatusPage /> },
  {
    // Everything below requires an approved account; the server enforces the same rules on every request.
    element: <RequireApprovedUser />,
    children: [
      {
        element: <AppShell />,
        children: [
          { path: '/', element: <OverviewPage /> },
          { path: '/stocks', element: <StocksPage /> },
          { path: '/stocks/:symbol', element: <StockPage /> },
          { path: '/watchlist', element: <WatchlistPage /> },
          { path: '/news', element: <NewsPage /> },
          { path: '/holdings', element: <HoldingsPage /> },
          { path: '/optimize', element: <OptimizePage /> },
          { path: '/optimize/:runId', element: <OptimizePage /> },
          { path: '/history', element: <HistoryPage /> },
          { path: '/reality-check/:runId', element: <RealityCheckPage /> },
          { path: '/machine-learning', element: <MachineLearningPage /> },
          { path: '/machine-learning/:experimentId', element: <MachineLearningPage /> },
          { path: '/strategy-comparison', element: <StrategyComparisonPage /> },
          { path: '/strategy-comparison/:comparisonId', element: <StrategyComparisonPage /> },
          { path: '/trades', element: <TradesPage /> },
          { path: '/analytics', element: <AnalyticsPage /> },
          { path: '/backtest', element: <BacktestPage /> },
          { path: '/backtest/:backtestId', element: <BacktestPage /> },
          { path: '/market-data', element: <MarketDataPage /> },
          { path: '/settings', element: <SettingsPage /> },
          { path: '/learn', element: <LearnIndexPage /> },
          { path: '/learn/glossary', element: <GlossaryPage /> },
          { path: '/learn/:strategyId', element: <StrategyGuidePage /> },
          {
            element: <RequireAdministrator />,
            children: [
              { path: '/admin', element: <AdminOverviewPage /> },
              { path: '/admin/users', element: <AdminUsersPage /> },
              { path: '/admin/settings', element: <AdminSettingsPage /> },
            ],
          },
          { path: '*', element: <NotFoundPage /> },
        ],
      },
    ],
  },
])

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <TooltipProvider delay={200}>
        <AuthProvider>
          <SelectionProvider>
            <RouterProvider router={router} />
          </SelectionProvider>
        </AuthProvider>
      </TooltipProvider>
      <Toaster richColors position="bottom-right" />
    </QueryClientProvider>
  </StrictMode>,
)
