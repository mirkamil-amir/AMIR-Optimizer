import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import type {
  AppSettings, Asset, AuditEvent, Backtest, BacktestRequest, BacktestSummary, CashEntry, CatalogueImportResult, HoldingsResponse,
  MarketDataStatus, OptimizationRun, OptimizationRunSummary, Order, PagedResult, PaperTradeResult, Performance, Portfolio,
  PortfolioSettings, PortfolioSummary, PriceBar, Risk, RunStatus, Sector, StockDetail, StockListItem, SyncJob, TradeProposal,
  Transaction, UserAccountStatus, UserSummary, MlCatalog, MlExperiment, MlExperimentRequest, MlExperimentSummary, MlBacktest,
  MlPredictionSet, StrategyComparison, StrategyComparisonRequest, StrategyComparisonSummary, Market, QuoteRefreshRun, QuoteRefreshResult,
  AdminOverview,
} from './types'

const V1 = '/api/v1'

export const keys = {
  portfolios: ['portfolios'] as const,
  portfolio: (id: string) => ['portfolio', id] as const,
  holdings: (id: string) => ['holdings', id] as const,
  transactions: (id: string, page: number, symbol?: string) => ['transactions', id, page, symbol ?? ''] as const,
  orders: (id: string, page: number) => ['orders', id, page] as const,
  cash: (id: string, page: number) => ['cash', id, page] as const,
  proposals: (id: string) => ['proposals', id] as const,
  runs: (id: string) => ['runs', id] as const,
  run: (id: string) => ['run', id] as const,
  performance: (id: string, from?: string) => ['performance', id, from ?? ''] as const,
  risk: (id: string) => ['risk', id] as const,
  status: ['market-status'] as const,
  assets: ['assets'] as const,
  prices: (symbol: string, from?: string) => ['prices', symbol, from ?? ''] as const,
  settings: ['settings'] as const,
  backtests: ['backtests'] as const,
  backtest: (id: string) => ['backtest', id] as const,
  job: (id: string) => ['job', id] as const,
  history: (page: number, portfolioId?: string, status?: string) => ['history', page, portfolioId ?? '', status ?? ''] as const,
  sectors: ['sectors'] as const,
  stocks: (params: string) => ['stocks', params] as const,
  stock: (symbol: string) => ['stock', symbol] as const,
  users: (status?: string, search?: string, page = 1) => ['admin-users', status ?? '', search ?? '', page] as const,
  audit: (page: number, action?: string) => ['admin-audit', page, action ?? ''] as const,
}

// ---------------------------------------------------------------- portfolios

export const usePortfolios = () => useQuery({ queryKey: keys.portfolios, queryFn: () => api.get<PortfolioSummary[]>(`${V1}/portfolios`) })

export const usePortfolio = (id?: string) =>
  useQuery({ queryKey: keys.portfolio(id ?? ''), queryFn: () => api.get<Portfolio>(`${V1}/portfolios/${id}`), enabled: !!id })

export const useHoldings = (id?: string) =>
  useQuery({ queryKey: keys.holdings(id ?? ''), queryFn: () => api.get<HoldingsResponse>(`${V1}/portfolios/${id}/holdings`), enabled: !!id })

export const useTransactions = (id: string | undefined, page = 1, symbol?: string, pageSize = 25) =>
  useQuery({
    queryKey: keys.transactions(id ?? '', page, symbol),
    queryFn: () => api.get<PagedResult<Transaction>>(`${V1}/portfolios/${id}/transactions?page=${page}&pageSize=${pageSize}${symbol ? `&symbol=${symbol}` : ''}`),
    enabled: !!id,
    placeholderData: keepPreviousData,
  })

export const useOrders = (id: string | undefined, page = 1) =>
  useQuery({
    queryKey: keys.orders(id ?? '', page),
    queryFn: () => api.get<PagedResult<Order>>(`${V1}/portfolios/${id}/orders?page=${page}&pageSize=25`),
    enabled: !!id,
    placeholderData: keepPreviousData,
  })

export const useCashLedger = (id: string | undefined, page = 1) =>
  useQuery({
    queryKey: keys.cash(id ?? '', page),
    queryFn: () => api.get<PagedResult<CashEntry>>(`${V1}/portfolios/${id}/cash-ledger?page=${page}&pageSize=25`),
    enabled: !!id,
    placeholderData: keepPreviousData,
  })

export const useProposals = (id?: string) =>
  useQuery({
    queryKey: keys.proposals(id ?? ''),
    queryFn: () => api.get<PagedResult<TradeProposal>>(`${V1}/portfolios/${id}/trade-proposals?pageSize=20`),
    enabled: !!id,
  })

export const usePerformance = (id?: string, from?: string) =>
  useQuery({
    queryKey: keys.performance(id ?? '', from),
    queryFn: () => api.get<Performance>(`${V1}/portfolios/${id}/performance${from ? `?from=${from}` : ''}`),
    enabled: !!id,
    placeholderData: keepPreviousData,
  })

export const useRisk = (id?: string) =>
  useQuery({ queryKey: keys.risk(id ?? ''), queryFn: () => api.get<Risk>(`${V1}/portfolios/${id}/risk`), enabled: !!id })

export function useCreatePortfolio() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { name: string; initialInvestment: number; baseCurrency: string; settings?: PortfolioSettings }) =>
      api.post<Portfolio>(`${V1}/portfolios`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.portfolios }),
  })
}

export function useUpdatePortfolio(id: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { name: string; settings: PortfolioSettings }) => api.put<Portfolio>(`${V1}/portfolios/${id}`, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: keys.portfolio(id) })
      qc.invalidateQueries({ queryKey: keys.portfolios })
    },
  })
}

export function useDeletePortfolio() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`${V1}/portfolios/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.portfolios }),
  })
}

// ---------------------------------------------------------------- optimization & trading

export const useRuns = (id?: string) =>
  useQuery({
    queryKey: keys.runs(id ?? ''),
    queryFn: () => api.get<PagedResult<OptimizationRunSummary>>(`${V1}/portfolios/${id}/optimization-runs?pageSize=20`),
    enabled: !!id,
  })

export const useRun = (runId?: string) =>
  useQuery({ queryKey: keys.run(runId ?? ''), queryFn: () => api.get<OptimizationRun>(`${V1}/optimization-runs/${runId}`), enabled: !!runId })

/** Runs and stores an optimization. The settings are saved as portfolio defaults only when `persistSettings` is set. */
export function useRunOptimization(portfolioId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ settings, persistSettings }: { settings: PortfolioSettings; persistSettings: boolean }) =>
      api.post<OptimizationRun>(`${V1}/portfolios/${portfolioId}/optimization-runs`, { settings, persistSettings, frontierPoints: 30 }),
    onSuccess: (run) => {
      qc.setQueryData(keys.run(run.id), run)
      qc.invalidateQueries({ queryKey: keys.runs(portfolioId) })
      qc.invalidateQueries({ queryKey: keys.portfolio(portfolioId) })
      qc.invalidateQueries({ queryKey: ['history'] })
    },
  })
}

/** Optimization history of the signed-in user across portfolios. */
export const useOptimizationHistory = (page: number, portfolioId?: string, status?: RunStatus) =>
  useQuery({
    queryKey: keys.history(page, portfolioId, status),
    queryFn: () => api.get<PagedResult<OptimizationRunSummary>>(
      `${V1}/optimization-runs?page=${page}&pageSize=25${portfolioId ? `&portfolioId=${portfolioId}` : ''}${status ? `&status=${status}` : ''}`),
    placeholderData: keepPreviousData,
  })

export function useCreateProposal() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (runId: string) => api.post<TradeProposal>(`${V1}/optimization-runs/${runId}/trade-proposals`),
    onSuccess: (p) => qc.invalidateQueries({ queryKey: keys.proposals(p.portfolioId) }),
  })
}

export function useRebalancePreview(portfolioId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { optimizationRunId?: string; targetWeights?: Record<string, number> }) =>
      api.post<TradeProposal>(`${V1}/portfolios/${portfolioId}/rebalance-preview`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.proposals(portfolioId) }),
  })
}

export function useExecuteProposal(portfolioId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ proposalId, idempotencyKey }: { proposalId: string; idempotencyKey: string }) =>
      api.post<PaperTradeResult>(`${V1}/portfolios/${portfolioId}/paper-trades`, { proposalId, confirm: true }, { 'Idempotency-Key': idempotencyKey }),
    onSuccess: () => {
      for (const k of ['holdings', 'transactions', 'orders', 'cash', 'proposals', 'performance', 'risk', 'portfolio']) {
        qc.invalidateQueries({ queryKey: [k, portfolioId] })
      }
      qc.invalidateQueries({ queryKey: keys.portfolios })
    },
  })
}

// ---------------------------------------------------------------- market data & settings

export const useMarketStatus = (poll = false) =>
  useQuery({
    queryKey: keys.status,
    queryFn: () => api.get<MarketDataStatus>(`${V1}/market-data/status`),
    refetchInterval: (q) => (poll || q.state.data?.recentJobs.some((j) => j.status === 'Queued' || j.status === 'Running') ? 2000 : 60_000),
  })

export const useAssets = () => useQuery({ queryKey: keys.assets, queryFn: () => api.get<Asset[]>(`${V1}/assets`) })

export const usePrices = (symbol?: string, from?: string) =>
  useQuery({
    queryKey: keys.prices(symbol ?? '', from),
    queryFn: () => api.get<PagedResult<PriceBar>>(`${V1}/assets/${symbol}/prices?pageSize=5000${from ? `&from=${from}` : ''}`),
    enabled: !!symbol,
  })

export function useSync() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { symbols?: string[]; startDate?: string; provider?: string; forceFullRefresh?: boolean }) =>
      api.post<SyncJob>(`${V1}/market-data/sync`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.status }),
  })
}

export function useCreateAsset() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { symbol: string; name?: string; currency: string; assetType: 'Equity' | 'Etf'; sector?: string }) =>
      api.post<Asset>(`${V1}/assets`, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: keys.assets })
      qc.invalidateQueries({ queryKey: keys.status })
    },
  })
}

export function useUpdateAsset() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ symbol, ...body }: { symbol: string; isEnabled?: boolean; sector?: string }) => api.patch<Asset>(`${V1}/assets/${symbol}`, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: keys.assets })
      qc.invalidateQueries({ queryKey: keys.status })
    },
  })
}

export const useSettings = () => useQuery({ queryKey: keys.settings, queryFn: () => api.get<AppSettings>(`${V1}/settings`) })

export function useUpdateSettings() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { activeProvider: string; defaultCurrency: string; defaultRiskFreeRate: number; defaultBenchmark: string; stalePriceDays: number }) =>
      api.put<AppSettings>(`${V1}/settings`, body),
    onSuccess: () => qc.invalidateQueries(),
  })
}

// ---------------------------------------------------------------- backtests

export const useBacktests = () =>
  useQuery({
    queryKey: keys.backtests,
    queryFn: () => api.get<PagedResult<BacktestSummary>>(`${V1}/backtests?pageSize=20`),
    refetchInterval: (q) => (q.state.data?.items.some((b) => b.status === 'Queued' || b.status === 'Running') ? 1500 : false),
  })

export const useBacktest = (id?: string) =>
  useQuery({
    queryKey: keys.backtest(id ?? ''),
    queryFn: () => api.get<Backtest>(`${V1}/backtests/${id}`),
    enabled: !!id,
    refetchInterval: (q) => (q.state.data && (q.state.data.status === 'Queued' || q.state.data.status === 'Running') ? 1000 : false),
  })

export function useCreateBacktest() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: BacktestRequest) => api.post<Backtest>(`${V1}/backtests`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.backtests }),
  })
}

// ---------------------------------------------------------------- stock discovery

export const useMarkets = () => useQuery({ queryKey: ['markets'], queryFn: () => api.get<Market[]>(`${V1}/markets`), staleTime: 60_000 })

export const useSectors = (filter: { country?: string; index?: string } = {}) => {
  const p = new URLSearchParams()
  if (filter.country) p.set('country', filter.country)
  if (filter.index) p.set('index', filter.index)
  const qs = p.toString()
  return useQuery({
    queryKey: [...keys.sectors, qs],
    queryFn: () => api.get<Sector[]>(`${V1}/sectors${qs ? `?${qs}` : ''}`),
    placeholderData: keepPreviousData,
  })
}

export type StockSort = 'symbol' | 'name' | 'sector' | 'change' | 'change-desc'

export interface StockQuery {
  country?: string
  index?: string
  sector?: string
  search?: string
  symbols?: string[]
  withPrices?: boolean
  sort?: StockSort
  page?: number
  pageSize?: number
}

function stockParams(q: StockQuery) {
  const p = new URLSearchParams()
  if (q.country) p.set('country', q.country)
  if (q.index) p.set('index', q.index)
  if (q.sort && q.sort !== 'symbol') p.set('sort', q.sort)
  if (q.sector) p.set('sector', q.sector)
  if (q.search) p.set('search', q.search)
  if (q.symbols?.length) p.set('symbols', q.symbols.join(','))
  if (q.withPrices !== undefined) p.set('withPrices', String(q.withPrices))
  p.set('page', String(q.page ?? 1))
  p.set('pageSize', String(q.pageSize ?? 50))
  return p.toString()
}

export const useStocks = (q: StockQuery, enabled = true) => {
  const params = stockParams(q)
  return useQuery({
    queryKey: keys.stocks(params),
    queryFn: ({ signal }) => api.get<PagedResult<StockListItem>>(`${V1}/stocks?${params}`, signal),
    placeholderData: keepPreviousData,
    enabled,
  })
}

export const useStock = (symbol?: string) =>
  useQuery({ queryKey: keys.stock(symbol ?? ''), queryFn: () => api.get<StockDetail>(`${V1}/stocks/${symbol}`), enabled: !!symbol })

/**
 * Downloads price history for selected symbols (approved users: up to 50 named symbols) and waits for the background job,
 * so coverage warnings and stock lists update as soon as the history is stored.
 */
export function useFetchPrices() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (symbols: string[]) => {
      let job = await api.post<SyncJob>(`${V1}/market-data/sync`, { symbols })
      for (let i = 0; i < 120 && (job.status === 'Queued' || job.status === 'Running'); i++) {
        await new Promise((resolve) => setTimeout(resolve, 1500))
        job = await api.get<SyncJob>(`${V1}/market-data/jobs/${job.id}`)
      }
      return job
    },
    onSettled: () => {
      qc.invalidateQueries({ queryKey: keys.status })
      qc.invalidateQueries({ queryKey: ['stocks'] })
      qc.invalidateQueries({ queryKey: ['stock'] })
      qc.invalidateQueries({ queryKey: ['markets'] })
      qc.invalidateQueries({ queryKey: keys.assets })
    },
  })
}

/** Latest background quote refresh cycle (started at sign-in); polls while it is running. */
export const useQuoteRefreshCycle = () =>
  useQuery({
    queryKey: ['quote-refresh', 'latest'],
    queryFn: () => api.get<QuoteRefreshRun | undefined>(`${V1}/market-data/quote-refresh/latest`),
    refetchInterval: (q) => (q.state.data && (q.state.data.status === 'Queued' || q.state.data.status === 'Running') ? 3000 : false),
  })

/** Refreshes quotes for the given symbols through the backend; refreshed rows are written into cached stock lists. */
export function useRefreshQuotes() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { symbols: string[]; force?: boolean }) => api.post<QuoteRefreshResult>(`${V1}/stocks/quotes/refresh`, body),
    onSuccess: (result) => mergeRefreshedStocks(qc, result),
  })
}

/** Refreshes a single stock's quote now (server applies a short cooldown). */
export function useRefreshQuote() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (symbol: string) => api.post<QuoteRefreshResult>(`${V1}/stocks/${encodeURIComponent(symbol)}/quote/refresh`),
    onSuccess: (result) => {
      mergeRefreshedStocks(qc, result)
      for (const s of result.stocks) qc.invalidateQueries({ queryKey: keys.stock(s.symbol) })
    },
  })
}

function mergeRefreshedStocks(qc: ReturnType<typeof useQueryClient>, result: QuoteRefreshResult) {
  const bySymbol = new Map(result.stocks.map((s) => [s.symbol, s]))
  qc.setQueriesData<PagedResult<StockListItem>>({ queryKey: ['stocks'] }, (old) =>
    old ? { ...old, items: old.items.map((s) => bySymbol.get(s.symbol) ?? s) } : old)
}

export const useSyncJob = (id?: string) =>
  useQuery({
    queryKey: keys.job(id ?? ''),
    queryFn: () => api.get<SyncJob>(`${V1}/market-data/jobs/${id}`),
    enabled: !!id,
    refetchInterval: (q) => (q.state.data && (q.state.data.status === 'Queued' || q.state.data.status === 'Running') ? 1000 : false),
  })

// ---------------------------------------------------------------- account & administration

export function useChangePassword() {
  return useMutation({
    mutationFn: (body: { currentPassword: string; newPassword: string }) => api.post<void>(`${V1}/auth/change-password`, body),
  })
}

export const useAdminUsers = (status?: UserAccountStatus, search?: string, page = 1, includeTestAccounts = false) =>
  useQuery({
    queryKey: [...keys.users(status, search, page), includeTestAccounts],
    queryFn: () => api.get<PagedResult<UserSummary>>(
      `${V1}/admin/users?page=${page}&pageSize=50${status ? `&status=${status}` : ''}${search ? `&search=${encodeURIComponent(search)}` : ''}` +
      `${includeTestAccounts ? '&includeTestAccounts=true' : ''}`),
    placeholderData: keepPreviousData,
  })

export const useAdminOverview = () =>
  useQuery({ queryKey: ['admin', 'overview'], queryFn: () => api.get<AdminOverview>(`${V1}/admin/overview`), refetchInterval: 15_000 })

export function useStartQuoteRefresh() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<QuoteRefreshRun>(`${V1}/admin/catalogue/quote-refresh`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['admin', 'overview'] })
      qc.invalidateQueries({ queryKey: ['quote-refresh'] })
    },
  })
}

export function useSetUserStatus() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, status, reason }: { id: string; status: UserAccountStatus; reason?: string }) =>
      api.put<UserSummary>(`${V1}/admin/users/${id}/status`, { status, reason }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['admin-users'] })
      qc.invalidateQueries({ queryKey: ['admin-audit'] })
    },
  })
}

export function useSetUserRoles() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, roles }: { id: string; roles: string[] }) => api.put<UserSummary>(`${V1}/admin/users/${id}/roles`, { roles }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['admin-users'] })
      qc.invalidateQueries({ queryKey: ['admin-audit'] })
    },
  })
}

export const useAuditEvents = (page: number, action?: string) =>
  useQuery({
    queryKey: keys.audit(page, action),
    queryFn: () => api.get<PagedResult<AuditEvent>>(`${V1}/admin/audit-events?page=${page}&pageSize=50${action ? `&action=${action}` : ''}`),
    placeholderData: keepPreviousData,
  })

export function useImportCatalogue() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { csv: string; source: string; track: boolean; overwriteMetadata: boolean }) =>
      api.post<CatalogueImportResult>(`${V1}/admin/catalogue/import`, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['stocks'] })
      qc.invalidateQueries({ queryKey: keys.sectors })
    },
  })
}

// ---------------------------------------------------------------- machine learning

const isActive = (s?: string) => s === 'Queued' || s === 'Running'

export const useMlCatalog = () => useQuery({ queryKey: ['ml-catalog'], queryFn: () => api.get<MlCatalog>(`${V1}/ml/models`), staleTime: Infinity })

export const useMlExperiments = (page = 1) =>
  useQuery({
    queryKey: ['ml-experiments', page],
    queryFn: () => api.get<PagedResult<MlExperimentSummary>>(`${V1}/ml/experiments?page=${page}&pageSize=20`),
    placeholderData: keepPreviousData,
    refetchInterval: (q) => (q.state.data?.items.some((e) => isActive(e.status)) ? 2000 : false),
  })

export const useMlExperiment = (id?: string) =>
  useQuery({
    queryKey: ['ml-experiment', id ?? ''],
    queryFn: () => api.get<MlExperiment>(`${V1}/ml/experiments/${id}`),
    enabled: !!id,
    refetchInterval: (q) => (isActive(q.state.data?.summary.status) ? 1500 : false),
  })

export const useMlBacktest = (id?: string, enabled = true) =>
  useQuery({ queryKey: ['ml-backtest', id ?? ''], queryFn: () => api.get<MlBacktest>(`${V1}/ml/experiments/${id}/backtest`), enabled: !!id && enabled })

export function useCreateMlExperiment() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: MlExperimentRequest) => api.post<MlExperiment>(`${V1}/ml/experiments`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['ml-experiments'] }),
  })
}

export function usePredictAgain(id: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<MlPredictionSet>(`${V1}/ml/experiments/${id}/predictions`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['ml-experiment', id] }),
  })
}

export function useDeleteMlExperiment() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`${V1}/ml/experiments/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['ml-experiments'] }),
  })
}

// ---------------------------------------------------------------- strategy comparison

export const useComparisons = (page = 1) =>
  useQuery({
    queryKey: ['comparisons', page],
    queryFn: () => api.get<PagedResult<StrategyComparisonSummary>>(`${V1}/strategy-comparisons?page=${page}&pageSize=20`),
    placeholderData: keepPreviousData,
    refetchInterval: (q) => (q.state.data?.items.some((e) => isActive(e.status)) ? 2000 : false),
  })

export const useComparison = (id?: string) =>
  useQuery({
    queryKey: ['comparison', id ?? ''],
    queryFn: () => api.get<StrategyComparison>(`${V1}/strategy-comparisons/${id}`),
    enabled: !!id,
    refetchInterval: (q) => (isActive(q.state.data?.summary.status) ? 1500 : false),
  })

export function useCreateComparison() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: StrategyComparisonRequest) => api.post<StrategyComparison>(`${V1}/strategy-comparisons`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['comparisons'] }),
  })
}

export function useDeleteComparison() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`${V1}/strategy-comparisons/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['comparisons'] }),
  })
}
