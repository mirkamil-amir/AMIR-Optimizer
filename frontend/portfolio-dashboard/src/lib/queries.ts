import { keepPreviousData, useQueries, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import type {
  AppSettings, Asset, AuditEvent, Backtest, BacktestRequest, BacktestSummary, CashEntry, CatalogueImportResult, HoldingsResponse,
  MarketDataStatus, OptimizationRun, OptimizationRunSummary, Order, PagedResult, PaperTradeResult, Performance, Portfolio,
  PortfolioSettings, PortfolioSummary, PriceBar, Risk, RunStatus, Sector, StockDetail, StockListItem, SyncJob, TradeProposal,
  Transaction, UserAccountStatus, UserSummary, MlCatalog, MlExperiment, MlExperimentRequest, MlExperimentSummary, MlBacktest,
  MlPredictionSet, StrategyComparison, StrategyComparisonRequest, StrategyComparisonSummary, Market, QuoteRefreshRun, QuoteRefreshResult,
  AdminOverview, HistoryRange, PriceHistory, Watchlist, WatchlistAddResult, NewsFeed, NewsFilters, EventComparison, RealityCheck, CompanyProfile,
  FxInfo, FxRate, FxHistory, FxBoard, ResearchSession, ResearchSessionSummary, UpdateBriefRequest, HypothesisComparison, CompanyContext, RefinementChange,
  PortfolioOverview, PortfolioTransaction, RecordTransactionRequest, MarketDataSummary, DataCheck, DataCheckRequest, DataDownload,
  PortfolioStrategy, StrategyBacktestRequest,
} from './types'

const V1 = '/api/v1'

export const keys = {
  portfolios: ['portfolios'] as const,
  portfolio: (id: string) => ['portfolio', id] as const,
  overview: (currency?: string) => ['portfolios', 'overview', currency ?? ''] as const,
  journal: (id: string, page: number) => ['journal', id, page] as const,
  holdings: (id: string, currency?: string) => ['holdings', id, currency ?? ''] as const,
  transactions: (id: string, page: number, symbol?: string) => ['transactions', id, page, symbol ?? ''] as const,
  orders: (id: string, page: number) => ['orders', id, page] as const,
  cash: (id: string, page: number) => ['cash', id, page] as const,
  proposals: (id: string) => ['proposals', id] as const,
  runs: (id: string) => ['runs', id] as const,
  run: (id: string) => ['run', id] as const,
  performance: (id: string, from?: string, currency?: string) => ['performance', id, from ?? '', currency ?? ''] as const,
  risk: (id: string) => ['risk', id] as const,
  status: ['market-status'] as const,
  // Under the status key, so every invalidation of the status also refreshes the summary.
  summary: ['market-status', 'summary'] as const,
  assets: ['assets'] as const,
  prices: (symbol: string, from?: string) => ['prices', symbol, from ?? ''] as const,
  settings: ['settings'] as const,
  backtests: ['backtests'] as const,
  backtest: (id: string) => ['backtest', id] as const,
  job: (id: string) => ['job', id] as const,
  history: (page: number, portfolioId?: string, status?: string, pageSize = 25) => ['history', page, portfolioId ?? '', status ?? '', pageSize] as const,
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

/** Holdings valued in the portfolio currency, or in `currency` (a reporting conversion that changes nothing). */
export const useHoldings = (id?: string, currency?: string) =>
  useQuery({
    queryKey: keys.holdings(id ?? '', currency),
    queryFn: () => api.get<HoldingsResponse>(`${V1}/portfolios/${id}/holdings${currency ? `?currency=${currency}` : ''}`),
    enabled: !!id,
    placeholderData: keepPreviousData,
  })

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

export const usePerformance = (id?: string, from?: string, currency?: string) =>
  useQuery({
    queryKey: keys.performance(id ?? '', from, currency),
    queryFn: () => {
      const q = new URLSearchParams()
      if (from) q.set('from', from)
      if (currency) q.set('currency', currency)
      return api.get<Performance>(`${V1}/portfolios/${id}/performance${q.size ? `?${q}` : ''}`)
    },
    enabled: !!id,
    placeholderData: keepPreviousData,
  })

export const useRisk = (id?: string) =>
  useQuery({ queryKey: keys.risk(id ?? ''), queryFn: () => api.get<Risk>(`${V1}/portfolios/${id}/risk`), enabled: !!id })

/** All of the user's portfolios with a total over the active ones in `currency` (default: application currency). */
export const usePortfolioOverview = (currency?: string) =>
  useQuery({
    queryKey: keys.overview(currency),
    queryFn: () => api.get<PortfolioOverview>(`${V1}/portfolios/overview${currency ? `?currency=${currency}` : ''}`),
    placeholderData: keepPreviousData,
  })

export interface CreatePortfolioBody {
  name: string
  description?: string
  initialInvestment: number
  baseCurrency: string
  settings?: PortfolioSettings
  /** Working universe for the optimizer (not holdings). */
  universe?: string[]
  maxWeight?: number
}

export function useCreatePortfolio() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: CreatePortfolioBody) => api.post<Portfolio>(`${V1}/portfolios`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.portfolios }),
  })
}

/** Everything that depends on one portfolio's state – refreshed after a booking or lifecycle change. */
function invalidatePortfolio(qc: ReturnType<typeof useQueryClient>, id: string) {
  qc.invalidateQueries({ queryKey: keys.portfolios })
  for (const k of ['portfolio', 'holdings', 'transactions', 'orders', 'cash', 'proposals', 'performance', 'risk', 'journal']) {
    qc.invalidateQueries({ queryKey: [k, id] })
  }
}

export function useUpdatePortfolioDetails(id: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: { name: string; description?: string | null }) => api.put<Portfolio>(`${V1}/portfolios/${id}/details`, body),
    onSuccess: () => invalidatePortfolio(qc, id),
  })
}

export function useArchivePortfolio() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, archive }: { id: string; archive: boolean }) => api.post<Portfolio>(`${V1}/portfolios/${id}/${archive ? 'archive' : 'restore'}`),
    onSuccess: (p) => invalidatePortfolio(qc, p.id),
  })
}

export const useJournal = (id?: string, page = 1, pageSize = 50) =>
  useQuery({
    queryKey: [...keys.journal(id ?? '', page), pageSize],
    queryFn: () => api.get<PagedResult<PortfolioTransaction>>(`${V1}/portfolios/${id}/journal?page=${page}&pageSize=${pageSize}`),
    enabled: !!id,
  })

/** Books a manual transaction. One idempotency key per submitted form, so a double click books once. */
export function useRecordTransaction(id: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ body, idempotencyKey }: { body: RecordTransactionRequest; idempotencyKey: string }) =>
      api.post<PortfolioTransaction>(`${V1}/portfolios/${id}/journal`, body, { 'Idempotency-Key': idempotencyKey }),
    onSuccess: () => invalidatePortfolio(qc, id),
  })
}

export function useReverseTransaction(id: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ transactionId, idempotencyKey }: { transactionId: string; idempotencyKey: string }) =>
      api.post<PortfolioTransaction>(`${V1}/portfolios/${id}/journal/${transactionId}/reverse`, {}, { 'Idempotency-Key': idempotencyKey }),
    onSuccess: () => invalidatePortfolio(qc, id),
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

/** Refreshes the quotes of one portfolio's open positions (the server reuses quotes inside its cache window). */
export function useRefreshPortfolioQuotes() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.post<QuoteRefreshResult>(`${V1}/portfolios/${id}/quotes/refresh`),
    onSuccess: (result, id) => {
      mergeRefreshedStocks(qc, result)
      invalidatePortfolio(qc, id)
    },
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
export const useOptimizationHistory = (page: number, portfolioId?: string, status?: RunStatus, pageSize = 25) =>
  useQuery({
    queryKey: keys.history(page, portfolioId, status, pageSize),
    queryFn: () => api.get<PagedResult<OptimizationRunSummary>>(
      `${V1}/optimization-runs?page=${page}&pageSize=${pageSize}${portfolioId ? `&portfolioId=${portfolioId}` : ''}${status ? `&status=${status}` : ''}`),
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

/** Compact data status (counts, latest retrieval, running download) for the navigation and dashboards. */
export const useMarketSummary = () =>
  useQuery({
    queryKey: keys.summary,
    queryFn: () => api.get<MarketDataSummary>(`${V1}/market-data/summary`),
    refetchInterval: (q) => (q.state.data?.syncRunning ? 2000 : 60_000),
  })

export const useAssets = () => useQuery({ queryKey: keys.assets, queryFn: () => api.get<Asset[]>(`${V1}/assets`) })

export const usePrices = (symbol?: string, from?: string) =>
  useQuery({
    queryKey: keys.prices(symbol ?? '', from),
    queryFn: () => api.get<PagedResult<PriceBar>>(`${V1}/assets/${encodeURIComponent(symbol!)}/prices?pageSize=5000${from ? `&from=${from}` : ''}`),
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
    mutationFn: (body: { symbol: string; name?: string; currency?: string; assetType: 'Equity' | 'Etf'; sector?: string }) =>
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
    mutationFn: ({ symbol, ...body }: { symbol: string; isEnabled?: boolean; sector?: string }) => api.patch<Asset>(`${V1}/assets/${encodeURIComponent(symbol)}`, body),
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

/** The user's backtests; with `portfolioId` only those of that portfolio (its optimizations and custom backtests started in it). */
export const useBacktests = (portfolioId?: string) =>
  useQuery({
    queryKey: [...keys.backtests, portfolioId ?? ''],
    queryFn: () => api.get<PagedResult<BacktestSummary>>(`${V1}/backtests?pageSize=20${portfolioId ? `&portfolioId=${portfolioId}` : ''}`),
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

/** Backtest of a stored optimization (mathematical run or ML experiment) of a portfolio. */
export function useCreateStrategyBacktest() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: StrategyBacktestRequest) => api.post<Backtest>(`${V1}/backtests/from-strategy`, body),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.backtests }),
  })
}

/** The portfolio's optimizations that can be backtested or compared (successful runs and linked ML experiments). */
export const usePortfolioStrategies = (portfolioId: string | undefined) => useQuery({
  queryKey: ['portfolio-strategies', portfolioId],
  queryFn: () => api.get<PortfolioStrategy[]>(`${V1}/portfolios/${portfolioId}/strategies`),
  enabled: !!portfolioId,
})

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
  useQuery({
    queryKey: keys.stock(symbol ?? ''),
    queryFn: ({ signal }) => api.get<StockDetail>(`${V1}/stocks/${encodeURIComponent(symbol!)}`, signal),
    enabled: !!symbol,
  })

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

/** Stored daily prices per symbol for a whole selection (any size; one request in the body). */
export const useHistoryCoverage = (symbols: string[]) =>
  useQuery({
    queryKey: ['history-coverage', [...symbols].sort().join(',')],
    queryFn: () => api.post<{ symbol: string; observations: number }[]>(`${V1}/stocks/history-coverage`, { symbols }),
    enabled: symbols.length > 0,
    placeholderData: keepPreviousData,
  })

/** Queues price-history downloads in jobs of at most 50 symbols (the per-request limit); returns the job ids at once. */
export function useStartPriceDownload() {
  return useMutation({
    mutationFn: async (symbols: string[]) => {
      const ids: string[] = []
      for (let i = 0; i < symbols.length; i += 50) ids.push((await api.post<SyncJob>(`${V1}/market-data/sync`, { symbols: symbols.slice(i, i + 50) })).id)
      return ids
    },
  })
}

/** The given download jobs, polled every 1.2 s while one of them is queued or running (shows progress per stock). */
export function useSyncJobs(ids: string[]) {
  return useQueries({
    queries: ids.map((id) => ({
      queryKey: ['sync-job', id],
      queryFn: () => api.get<SyncJob>(`${V1}/market-data/jobs/${id}`),
      refetchInterval: (q: { state: { data?: SyncJob } }) => (!q.state.data || q.state.data.status === 'Queued' || q.state.data.status === 'Running' ? 1200 : false),
    })),
  })
}

/** After downloads: lists, coverage and the stock pages pick up the new prices. */
export function useAfterPriceDownload() {
  const qc = useQueryClient()
  return () => {
    for (const key of [keys.status, ['stocks'], ['stock'], ['markets'], keys.assets, ['history-coverage']]) qc.invalidateQueries({ queryKey: key })
  }
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

/** The user's ML experiments; `filter.portfolioId` = of one portfolio, `filter.unlinked` = those without a portfolio. */
export const useMlExperiments = (page = 1, filter: { portfolioId?: string; unlinked?: boolean; pageSize?: number } = {}) =>
  useQuery({
    queryKey: ['ml-experiments', page, filter.portfolioId ?? '', filter.unlinked ?? false, filter.pageSize ?? 20],
    queryFn: () => api.get<PagedResult<MlExperimentSummary>>(
      `${V1}/ml/experiments?page=${page}&pageSize=${filter.pageSize ?? 20}${filter.portfolioId ? `&portfolioId=${filter.portfolioId}` : ''}${filter.unlinked ? '&unlinked=true' : ''}`),
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

/** Links an ML experiment to one of the user's portfolios (null unlinks); the currencies must match. */
export function useAssignExperimentPortfolio(id: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (portfolioId: string | null) => api.put<MlExperiment>(`${V1}/ml/experiments/${id}/portfolio`, { portfolioId }),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['ml-experiment', id] })
      qc.invalidateQueries({ queryKey: ['ml-experiments'] })
      qc.invalidateQueries({ queryKey: ['portfolio-strategies'] })
    },
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
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['ml-experiments'] })
      qc.invalidateQueries({ queryKey: ['portfolio-strategies'] })
    },
  })
}

/** Deletes a Math Optimization (proposals made from it stay; backtests and comparisons keep their frozen settings). */
export function useDeleteOptimizationRun() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`${V1}/optimization-runs/${id}`),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['history'] })
      qc.invalidateQueries({ queryKey: ['runs'] })
      qc.invalidateQueries({ queryKey: ['portfolio-strategies'] })
    },
  })
}

// ---------------------------------------------------------------- strategy comparison

export const useComparisons = (page = 1, portfolioId?: string) =>
  useQuery({
    queryKey: ['comparisons', page, portfolioId ?? ''],
    queryFn: () => api.get<PagedResult<StrategyComparisonSummary>>(
      `${V1}/strategy-comparisons?page=${page}&pageSize=20${portfolioId ? `&portfolioId=${portfolioId}` : ''}`),
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

// ---------------------------------------------------------------- watchlist & price history

export const useWatchlist = (range: HistoryRange = '1M') =>
  useQuery({
    queryKey: ['watchlist', range] as const,
    queryFn: () => api.get<Watchlist>(`${V1}/watchlist?range=${range}`),
    placeholderData: keepPreviousData,
  })

export function useAddToWatchlist() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (symbol: string) => api.post<WatchlistAddResult>(`${V1}/watchlist`, { symbol }),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['watchlist'] }),
  })
}

export function useRemoveFromWatchlist() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (symbol: string) => api.delete<void>(`${V1}/watchlist/${encodeURIComponent(symbol)}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['watchlist'] }),
  })
}

/** Refreshes all watched stocks' quotes (the server reuses quotes inside its cache window). */
export function useRefreshWatchlist() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: () => api.post<QuoteRefreshResult>(`${V1}/watchlist/quotes/refresh`),
    onSuccess: (result) => {
      mergeRefreshedStocks(qc, result)
      qc.invalidateQueries({ queryKey: ['watchlist'] })
    },
  })
}

/** Query string for the news feed (pure; empty filters are omitted). */
export function newsParams(f: NewsFilters): string {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(f)) {
    if (v === undefined || v === null || v === '' || v === false) continue
    p.set(k, String(v))
  }
  return p.toString()
}

export const useNews = (filters: NewsFilters, enabled = true) => {
  const params = newsParams(filters)
  return useQuery({
    queryKey: ['news', params] as const,
    queryFn: ({ signal }) => api.get<NewsFeed>(`${V1}/news?${params}`, signal),
    placeholderData: keepPreviousData,
    staleTime: 5 * 60_000,
    // While the server refreshes due sources in the background, check again shortly to show the new articles.
    refetchInterval: (q) => (q.state.data?.refreshing ? 3000 : false),
    enabled,
  })
}

export const useEventComparison = (articleId: string | undefined, symbol: string | undefined) =>
  useQuery({
    queryKey: ['news-event', articleId ?? '', symbol ?? ''] as const,
    queryFn: () => api.get<EventComparison>(`${V1}/news/${articleId}/price-movement?symbol=${encodeURIComponent(symbol!)}`),
    enabled: !!articleId && !!symbol,
  })

export const useRealityCheck = (runId: string | undefined) =>
  useQuery({
    queryKey: ['reality-check', runId ?? ''] as const,
    queryFn: () => api.get<RealityCheck>(`${V1}/optimization-runs/${runId}/reality-check`),
    enabled: !!runId,
    staleTime: 5 * 60_000,
  })

export const useStockHistory = (symbol: string | undefined, range: HistoryRange) =>
  useQuery({
    queryKey: ['stock-history', symbol ?? '', range] as const,
    queryFn: ({ signal }) => api.get<PriceHistory>(`${V1}/stocks/${encodeURIComponent(symbol!)}/history?range=${range}`, signal),
    enabled: !!symbol,
    // Keep the old chart only while switching ranges of the same stock – never show one stock's prices under another's name.
    placeholderData: (previous, previousQuery) => (previousQuery?.queryKey[1] === (symbol ?? '') ? previous : undefined),
  })

// ---------------------------------------------------------------- AI research workflow

export const useResearchSessions = () =>
  useQuery({ queryKey: ['research'] as const, queryFn: () => api.get<ResearchSessionSummary[]>(`${V1}/research`) })

export const useResearchSession = (id: string | undefined) =>
  useQuery({ queryKey: ['research', id ?? ''] as const, queryFn: () => api.get<ResearchSession>(`${V1}/research/${id}`), enabled: !!id })

/** All research actions return the updated session, which replaces the cached copy (no stale mixture of steps). */
function useResearchAction<TArgs>(call: (args: TArgs) => Promise<ResearchSession>) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: call,
    onSuccess: (session) => {
      qc.setQueryData(['research', session.id], session)
      qc.invalidateQueries({ queryKey: ['research'], exact: true })
    },
  })
}

export const useStartResearch = () =>
  useResearchAction((body: { objective?: string; market?: string; riskPreference?: string; horizonYears?: number; portfolioId?: string }) => api.post<ResearchSession>(`${V1}/research`, body))
export const useUpdateBrief = (id: string) => useResearchAction((body: UpdateBriefRequest) => api.put<ResearchSession>(`${V1}/research/${id}/brief`, body))
export const useSetUniverse = (id: string) => useResearchAction((symbols: string[]) => api.put<ResearchSession>(`${V1}/research/${id}/universe`, { symbols }))
export const useEvaluateResearch = (id: string) => useResearchAction(() => api.post<ResearchSession>(`${V1}/research/${id}/evaluate`))
export const useProposeRefinement = (id: string) =>
  useResearchAction((body: { text?: string; hypothesisId?: string; changes?: RefinementChange[] }) => api.post<ResearchSession>(`${V1}/research/${id}/refinements`, body))
export const useApplyRefinement = (id: string) =>
  useResearchAction((proposalId: string) => api.post<ResearchSession>(`${V1}/research/${id}/refinements/${proposalId}/apply`))
export const useDiscardRefinement = (id: string) => useResearchAction(() => api.delete<ResearchSession>(`${V1}/research/${id}/refinements`))
export const useSaveHypothesis = (id: string) =>
  useResearchAction((body: { hypothesisId: string; portfolioId?: string; confirm: boolean }) => api.post<ResearchSession>(`${V1}/research/${id}/save`, body))

export function useDeleteResearch() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api.delete<void>(`${V1}/research/${id}`),
    onSuccess: () => qc.invalidateQueries({ queryKey: ['research'] }),
  })
}

export const useCompareHypotheses = (id: string, a: string | undefined, b: string | undefined) =>
  useQuery({
    queryKey: ['research-compare', id, a ?? '', b ?? ''] as const,
    queryFn: () => api.get<HypothesisComparison>(`${V1}/research/${id}/compare?a=${a}&b=${b}`),
    enabled: !!a && !!b && a !== b,
  })

export const useCompanyContext = (id: string, symbol: string | undefined) =>
  useQuery({
    queryKey: ['research-company', id, symbol ?? ''] as const,
    queryFn: () => api.get<CompanyContext>(`${V1}/research/${id}/companies/${encodeURIComponent(symbol!)}`),
    enabled: !!symbol,
    staleTime: 10 * 60_000,
  })

/** Company description (cached on the server; descriptions change rarely, so the browser keeps them for an hour). */
export const useCompanyProfile = (symbol: string | undefined) =>
  useQuery({
    queryKey: ['company-profile', symbol ?? ''] as const,
    queryFn: ({ signal }) => api.get<CompanyProfile>(`${V1}/stocks/${encodeURIComponent(symbol!)}/profile`, signal),
    enabled: !!symbol,
    staleTime: 60 * 60_000,
  })

// ---------------------------------------------------------------- exchange rates

/** The FX source, its convention and the currencies it can convert (changes rarely). */
export const useFxInfo = () =>
  useQuery({ queryKey: ['fx-info'] as const, queryFn: () => api.get<FxInfo>(`${V1}/fx/currencies`), staleTime: 60 * 60_000 })

/** Latest usable rate (units of `to` per one `from`); the query errors with the reason when none is available. */
export const useFxRate = (from: string | undefined, to: string | undefined) =>
  useQuery({
    queryKey: ['fx-rate', from ?? '', to ?? ''] as const,
    queryFn: () => api.get<FxRate>(`${V1}/fx/rate?from=${from}&to=${to}`),
    enabled: !!from && !!to && from !== to,
    staleTime: 15 * 60_000,
    retry: false,
  })

/** Published daily rates of a pair for charts; the previous pair stays visible while the next loads (superseded requests are aborted). */
export const useFxHistory = (from: string | undefined, to: string | undefined, days = 365) =>
  useQuery({
    queryKey: ['fx-history', from ?? '', to ?? '', days] as const,
    queryFn: ({ signal }) => api.get<FxHistory>(`${V1}/fx/history?from=${from}&to=${to}&days=${days}`, signal),
    enabled: !!from && !!to && from !== to,
    staleTime: 30 * 60_000,
    placeholderData: keepPreviousData,
  })

/** Latest reference rates of several currencies against `base`, with 1-day and 1-month changes (superseded requests are aborted). */
export const useFxBoard = (base: string | undefined, quotes: string[]) =>
  useQuery({
    queryKey: ['fx-board', base ?? '', quotes.join(',')] as const,
    queryFn: ({ signal }) => api.get<FxBoard>(`${V1}/fx/board?base=${base}&quotes=${quotes.join(',')}`, signal),
    enabled: !!base && quotes.length > 0,
    staleTime: 30 * 60_000,
  })

/** What an analysis would miss (stored prices and exchange rates); changes nothing. */
export const checkData = (request: DataCheckRequest) => api.post<DataCheck>(`${V1}/market-data/check`, request)

/** Queues price downloads and starts the exchange-rate download for what {@link checkData} reports as missing. */
export const downloadMissingData = (request: DataCheckRequest) => api.post<DataDownload>(`${V1}/market-data/download-missing`, request)

export const getSyncJob = (id: string) => api.get<SyncJob>(`${V1}/market-data/jobs/${id}`)
