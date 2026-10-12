import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import type {
  AppSettings, Asset, Backtest, BacktestRequest, BacktestSummary, CashEntry, HoldingsResponse, MarketDataStatus,
  OptimizationRun, OptimizationRunSummary, Order, PagedResult, PaperTradeResult, Performance, Portfolio,
  PortfolioSettings, PortfolioSummary, PriceBar, Risk, SyncJob, TradeProposal, Transaction,
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

export function useRunOptimization(portfolioId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (settings: PortfolioSettings) =>
      api.post<OptimizationRun>(`${V1}/portfolios/${portfolioId}/optimization-runs`, { settings, persistSettings: true, frontierPoints: 30 }),
    onSuccess: (run) => {
      qc.setQueryData(keys.run(run.id), run)
      qc.invalidateQueries({ queryKey: keys.runs(portfolioId) })
      qc.invalidateQueries({ queryKey: keys.portfolio(portfolioId) })
    },
  })
}

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
