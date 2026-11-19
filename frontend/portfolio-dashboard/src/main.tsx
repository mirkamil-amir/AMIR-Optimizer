import { StrictMode, lazy } from 'react'
import { createRoot } from 'react-dom/client'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { Navigate, createBrowserRouter, RouterProvider } from 'react-router'
import { Toaster } from '@/components/ui/sonner'
import { TooltipProvider } from '@/components/ui/tooltip'
import { AppShell } from '@/components/app-shell'
import { IntroGate } from '@/components/globe-intro'
import { RequireAdministrator, RequireApprovedUser } from '@/components/route-guards'
import { ApiError } from '@/lib/api'
import { AuthProvider } from '@/lib/auth'
import { SelectionProvider } from '@/lib/selection'
import { ThemeProvider } from '@/lib/theme'
import './index.css'

// Route-level code splitting keeps the initial bundle small.
const PilotToCurrent = lazy(() => import('@/pages/pilot').then((m) => ({ default: m.PilotToCurrent })))
const PilotPortfolioPage = lazy(() => import('@/pages/pilot').then((m) => ({ default: m.PilotPortfolioPage })))
const PilotPortfolioRedirect = lazy(() => import('@/pages/pilot').then((m) => ({ default: m.PilotPortfolioRedirect })))
const OverviewPage = lazy(() => import('@/pages/overview').then((m) => ({ default: m.OverviewPage })))
const PortfoliosPage = lazy(() => import('@/pages/portfolios').then((m) => ({ default: m.PortfoliosPage })))
const PortfolioWorkspace = lazy(() => import('@/pages/portfolio-workspace').then((m) => ({ default: m.PortfolioWorkspace })))
const ToCurrentPortfolio = lazy(() => import('@/pages/portfolio-workspace').then((m) => ({ default: m.ToCurrentPortfolio })))
const RunRedirect = lazy(() => import('@/pages/portfolio-workspace').then((m) => ({ default: m.RunRedirect })))
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
const ChoosePasswordPage = lazy(() => import('@/pages/auth').then((m) => ({ default: m.ChoosePasswordPage })))
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
  { path: '/choose-password', element: <ChoosePasswordPage /> },
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
              // The stock list of New portfolio without the creation form, to choose an optimization's universe.
              { path: 'optimize/stocks', element: <ChooseStocksPage section="optimize" /> },
              { path: 'ml-optimize/stocks', element: <ChooseStocksPage section="ml-optimize" /> },
              { path: 'optimize/:runId', element: <OptimizePage /> },
              { path: 'ml-optimize', element: <MlOptimizePage /> },
              { path: 'ml-optimize/:experimentId', element: <MlOptimizePage /> },
              // Matrix Pilot of this portfolio (D-67): Performance, Decisions, Mandate and Audit as sub-tabs.
              { path: 'pilot', element: <Navigate to="performance" replace /> },
              { path: 'pilot/performance', element: <PilotPortfolioPage section="performance" key="performance" /> },
              { path: 'pilot/decisions', element: <PilotPortfolioPage section="decisions" key="decisions" /> },
              { path: 'pilot/mandate', element: <PilotPortfolioPage section="mandate" key="mandate" /> },
              { path: 'pilot/audit', element: <PilotPortfolioPage section="audit" key="audit" /> },
              { path: 'transactions', element: <TradesPage /> },
              { path: 'runs', element: <HistoryPage /> },
              { path: 'backtest', element: <BacktestPage /> },
              { path: 'backtest/:backtestId', element: <BacktestPage /> },
              { path: 'comparison', element: <StrategyComparisonPage /> },
              { path: 'comparison/:comparisonId', element: <StrategyComparisonPage /> },
              // Performance (value, returns, risk, allocation, efficient frontier) is part of the Overview since 2026-10-24.
              { path: 'performance', element: <Navigate to=".." relative="path" replace /> },
              // Holdings (cards, currency exposure, positions table) are part of the Overview since 2026-10-26.
              { path: 'holdings', element: <Navigate to=".." relative="path" replace /> },
            ],
          },
          // Matrix Pilot is a portfolio tab in View (D-67): /pilot opens the current portfolio's Pilot (its own pilot first);
          // earlier Pilot and AI Probe URLs redirect there; the status of every portfolio's pilot is a column on Lens.
          { path: '/pilot', element: <PilotToCurrent section="performance" /> },
          { path: '/pilot/status', element: <Navigate to="/" replace /> },
          { path: '/pilot/performance', element: <PilotToCurrent section="performance" /> },
          { path: '/pilot/decisions', element: <PilotToCurrent section="decisions" /> },
          { path: '/pilot/mandate', element: <PilotToCurrent section="mandate" /> },
          { path: '/pilot/audit', element: <PilotToCurrent section="audit" /> },
          { path: '/pilot/:portfolioId', element: <PilotPortfolioRedirect /> },
          { path: '/pilot/:portfolioId/:section', element: <PilotPortfolioRedirect /> },
          { path: '/research', element: <Navigate to="/pilot" replace /> },
          { path: '/research/pilot', element: <Navigate to="/pilot" replace /> },
          { path: '/research/pilot/:portfolioId', element: <PilotPortfolioRedirect /> },
          { path: '/research/:sessionId', element: <Navigate to="/pilot" replace /> },
          { path: '/stocks', element: <StocksPage /> },
          { path: '/stocks/:symbol', element: <StockPage /> },
          { path: '/watchlist', element: <WatchlistPage /> },
          { path: '/news', element: <NewsPage /> },
          // Earlier portfolio-less URLs keep working: they open the section of the right portfolio.
          { path: '/holdings', element: <ToCurrentPortfolio section="" /> },
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
          // Market Data pages: Stocks & ETFs (above), Indexes and Currencies – one shared search-and-paging table.
          { path: '/market-data/:section', element: <MarketDataPage /> },
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
          <IntroGate>
            <SelectionProvider>
              <RouterProvider router={router} />
            </SelectionProvider>
          </IntroGate>
        </AuthProvider>
      </TooltipProvider>
      <Toaster richColors position="bottom-right" />
    </QueryClientProvider>
    </ThemeProvider>
  </StrictMode>,
)
