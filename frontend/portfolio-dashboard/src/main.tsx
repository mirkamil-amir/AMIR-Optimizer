import { StrictMode, lazy } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Navigate, createBrowserRouter, RouterProvider } from 'react-router'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { AppShell } from '@/components/app-shell'
import { RequireAdministrator, RequireApprovedUser } from '@/components/route-guards'
import { ApiError } from '@/lib/api'
import { AuthProvider } from '@/lib/auth'
import { SelectionProvider } from '@/lib/selection'
import { ThemeProvider } from '@/lib/theme'
import './index.css'

// Route-level code splitting keeps the initial bundle small.
const ResearchHomePage = lazy(() => import('@/pages/research').then((m) => ({ default: m.ResearchHomePage })))
const ResearchWorkspacePage = lazy(() => import('@/pages/research').then((m) => ({ default: m.ResearchWorkspacePage })))
const OverviewPage = lazy(() => import('@/pages/overview').then((m) => ({ default: m.OverviewPage })))
const PortfoliosPage = lazy(() => import('@/pages/portfolios').then((m) => ({ default: m.PortfoliosPage })))
const PortfolioWorkspace = lazy(() => import('@/pages/portfolio-workspace').then((m) => ({ default: m.PortfolioWorkspace })))
const ToCurrentPortfolio = lazy(() => import('@/pages/portfolio-workspace').then((m) => ({ default: m.ToCurrentPortfolio })))
const RunRedirect = lazy(() => import('@/pages/portfolio-workspace').then((m) => ({ default: m.RunRedirect })))
const HoldingsPage = lazy(() => import('@/pages/holdings').then((m) => ({ default: m.HoldingsPage })))
const OptimizePage = lazy(() => import('@/pages/optimize').then((m) => ({ default: m.OptimizePage })))
const ChooseStocksPage = lazy(() => import('@/pages/choose-stocks').then((m) => ({ default: m.ChooseStocksPage })))
const TradesPage = lazy(() => import('@/pages/trades').then((m) => ({ default: m.TradesPage })))
const BacktestPage = lazy(() => import('@/pages/backtest').then((m) => ({ default: m.BacktestPage })))
const MarketDataPage = lazy(() => import('@/pages/market-data').then((m) => ({ default: m.MarketDataPage })))
const SettingsPage = lazy(() => import('@/pages/settings').then((m) => ({ default: m.SettingsPage })))
const StocksPage = lazy(() => import('@/pages/stocks').then((m) => ({ default: m.StocksPage })))
const StockPage = lazy(() => import('@/pages/stock').then((m) => ({ default: m.StockPage })))
const WatchlistPage = lazy(() => import('@/pages/watchlist').then((m) => ({ default: m.WatchlistPage })))
const NewsPage = lazy(() => import('@/pages/news').then((m) => ({ default: m.NewsPage })))
const RealityCheckPage = lazy(() => import('@/pages/reality-check').then((m) => ({ default: m.RealityCheckPage })))
const HistoryPage = lazy(() => import('@/pages/history').then((m) => ({ default: m.HistoryPage })))
const MlOptimizePage = lazy(() => import('@/pages/machine-learning').then((m) => ({ default: m.MlOptimizePage })))
const ExperimentRedirect = lazy(() => import('@/pages/portfolio-workspace').then((m) => ({ default: m.ExperimentRedirect })))
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
          // Overview: all of the user's portfolios. Each portfolio has its own workspace with its id in the URL.
          { path: '/', element: <PortfoliosPage /> },
          {
            path: '/portfolios/:portfolioId',
            element: <PortfolioWorkspace />,
            children: [
              { index: true, element: <OverviewPage /> },
              { path: 'optimize', element: <OptimizePage /> },
              // The stock list of Portfolio Creation without the creation form, to choose an optimization's universe.
              { path: 'optimize/stocks', element: <ChooseStocksPage section="optimize" /> },
              { path: 'ml-optimize/stocks', element: <ChooseStocksPage section="ml-optimize" /> },
              { path: 'optimize/:runId', element: <OptimizePage /> },
              { path: 'ml-optimize', element: <MlOptimizePage /> },
              { path: 'ml-optimize/:experimentId', element: <MlOptimizePage /> },
              { path: 'holdings', element: <HoldingsPage /> },
              { path: 'transactions', element: <TradesPage /> },
              { path: 'runs', element: <HistoryPage /> },
              { path: 'backtest', element: <BacktestPage /> },
              { path: 'backtest/:backtestId', element: <BacktestPage /> },
              { path: 'comparison', element: <StrategyComparisonPage /> },
              { path: 'comparison/:comparisonId', element: <StrategyComparisonPage /> },
              // Performance (value, returns, risk, allocation, efficient frontier) is part of the Overview since 2026-10-24.
              { path: 'performance', element: <Navigate to=".." relative="path" replace /> },
            ],
          },
          { path: '/research', element: <ResearchHomePage /> },
          { path: '/research/:sessionId', element: <ResearchWorkspacePage /> },
          { path: '/stocks', element: <StocksPage /> },
          { path: '/stocks/:symbol', element: <StockPage /> },
          { path: '/watchlist', element: <WatchlistPage /> },
          { path: '/news', element: <NewsPage /> },
          // Earlier portfolio-less URLs keep working: they open the section of the right portfolio.
          { path: '/holdings', element: <ToCurrentPortfolio section="holdings" /> },
          { path: '/optimize', element: <ToCurrentPortfolio section="optimize" /> },
          { path: '/optimize/:runId', element: <RunRedirect /> },
          { path: '/history', element: <HistoryPage /> },
          { path: '/reality-check/:runId', element: <RealityCheckPage /> },
          // ML Optimization belongs to a portfolio like Math Optimization; earlier URLs open it inside the right portfolio.
          { path: '/ml-optimize', element: <ToCurrentPortfolio section="ml-optimize" /> },
          { path: '/machine-learning', element: <ToCurrentPortfolio section="ml-optimize" /> },
          { path: '/machine-learning/:experimentId', element: <ExperimentRedirect /> },
          { path: '/strategy-comparison', element: <StrategyComparisonPage /> },
          { path: '/strategy-comparison/:comparisonId', element: <StrategyComparisonPage /> },
          { path: '/trades', element: <ToCurrentPortfolio section="transactions" /> },
          { path: '/analytics', element: <ToCurrentPortfolio section="" /> },
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
    <ThemeProvider>
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
    </ThemeProvider>
  </StrictMode>,
)
