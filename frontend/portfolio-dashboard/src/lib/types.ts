// Mirrors the backend DTOs (PortfolioOptimizer.Application.Dtos). Weights, rates and returns are fractions.

export type ObjectiveKind = 'MinimumVariance' | 'MaximumSharpe' | 'TargetReturn' | 'MaximumReturnForRisk' | 'EqualWeight'
export type CovarianceKind = 'Sample' | 'LedoitWolf'
export type RebalanceKind = 'None' | 'Monthly' | 'Quarterly'
export type JobStatus = 'Queued' | 'Running' | 'Succeeded' | 'PartiallySucceeded' | 'Failed' | 'Cancelled'
export type RunStatus = 'Succeeded' | 'Infeasible' | 'InvalidInput' | 'Failed'
export type ProposalStatus = 'Pending' | 'Executed' | 'Superseded' | 'Cancelled'
export type ProposalAction = 'Buy' | 'Sell' | 'Hold' | 'NoPrice'
export type OrderSide = 'Buy' | 'Sell'
export type OrderStatus = 'Filled' | 'PartiallyFilled' | 'Rejected'
export type DataFreshness = 'Fresh' | 'Stale' | 'Missing'

export interface PagedResult<T> {
  items: T[]
  page: number
  pageSize: number
  totalCount: number
  totalPages: number
}

export interface Metric {
  value: number | null
  unavailableReason: string | null
}

export interface Asset {
  id: number
  symbol: string
  name: string | null
  exchange: string | null
  currency: string
  assetType: 'Equity' | 'Etf'
  sector: string | null
  isEnabled: boolean
}

export interface PriceBar {
  date: string
  open: number | null
  high: number | null
  low: number | null
  close: number
  adjustedClose: number | null
  volume: number | null
  source: string
  isSimulated: boolean
  retrievedAtUtc: string
}

export interface ProviderStatus {
  name: string
  displayName: string
  isSimulated: boolean
  requiresApiKey: boolean
  isConfigured: boolean
  providesAdjustedClose: boolean
  rateLimit: string
  terms: string
}

export interface SyncLog {
  symbol: string
  success: boolean
  rowsReceived: number
  rowsInserted: number
  rowsUpdated: number
  rowsRejected: number
  duplicatesSkipped: number
  message: string | null
  completedAtUtc: string
}

export interface SyncJob {
  id: string
  status: JobStatus
  source: string
  symbols: string[]
  startDate: string
  endDate: string
  requestedAtUtc: string
  startedAtUtc: string | null
  completedAtUtc: string | null
  assetsTotal: number
  assetsCompleted: number
  assetsFailed: number
  message: string | null
  logs: SyncLog[]
}

export interface AssetCoverage {
  assetId: number
  symbol: string
  name: string | null
  isEnabled: boolean
  source: string
  isSimulated: boolean
  observations: number
  firstDate: string | null
  lastDate: string | null
  hasAdjustedClose: boolean
  lastRetrievedAtUtc: string | null
  freshness: DataFreshness
  latestPrice: number | null
  latestPriceAsOfUtc: string | null
  lastSyncMessage: string | null
  lastSyncSuccess: boolean | null
}

export interface MarketDataStatus {
  activeProvider: string
  activeProviderIsSimulated: boolean
  providers: ProviderStatus[]
  coverage: AssetCoverage[]
  recentJobs: SyncJob[]
  stalePriceDays: number
  generatedAtUtc: string
}

export interface AppSettings {
  activeProvider: string
  defaultCurrency: string
  defaultRiskFreeRate: number
  defaultBenchmark: string
  stalePriceDays: number
  providers: ProviderStatus[]
  scheduledRefreshEnabled: boolean
  scheduledRefreshIntervalHours: number
}

export interface PortfolioSettings {
  universe: string[]
  lookbackDays: number
  objective: ObjectiveKind
  covarianceMethod: CovarianceKind
  riskFreeRate: number
  benchmarkSymbol: string
  minWeight: number
  maxWeight: number
  maxHoldings: number | null
  minPositionWeight: number | null
  cashReserve: number
  maxTurnover: number | null
  sectorLimits: Record<string, number>
  excludedSymbols: string[]
  targetReturn: number | null
  maxVolatility: number | null
  transactionCostBps: number
  minimumFee: number
  allowFractionalShares: boolean
  rebalanceFrequency: RebalanceKind
  rebalanceDriftThreshold: number
}

export interface PortfolioSummary {
  id: string
  name: string
  baseCurrency: string
  initialInvestment: number
  cashBalance: number
  marketValue: number
  totalValue: number
  holdings: number
  createdAtUtc: string
  updatedAtUtc: string
}

export interface Portfolio {
  id: string
  name: string
  baseCurrency: string
  initialInvestment: number
  cashBalance: number
  settings: PortfolioSettings
  createdAtUtc: string
  updatedAtUtc: string
  version: number
}

export interface Holding {
  assetId: number
  symbol: string
  name: string | null
  sector: string | null
  quantity: number
  averageCost: number
  costBasis: number
  latestPrice: number | null
  priceDate: string | null
  priceIsStale: boolean
  marketValue: number | null
  weight: number | null
  unrealizedPnl: number | null
  unrealizedReturn: number | null
  realizedPnl: number
  targetWeight: number | null
  weightDeviation: number | null
  dailyChange: number | null
  updatedAtUtc: string
}

export interface Valuation {
  marketValue: number
  cashBalance: number
  totalValue: number
  investedFraction: number
  totalCostBasis: number
  unrealizedPnl: number
  realizedPnl: number
  totalFees: number
  netContributions: number
  dailyChange: number | null
  dailyChangePercent: number | null
  totalReturn: number
  benchmarkReturn: number | null
  excessReturnVsBenchmark: number | null
  benchmarkSymbol: string
  valuationDate: string | null
  priceSource: string
  usesSimulatedData: boolean
  warnings: string[]
}

export interface HoldingsResponse {
  summary: Valuation
  holdings: Holding[]
}

export interface Transaction {
  id: string
  orderId: string
  symbol: string
  side: OrderSide
  quantity: number
  price: number
  priceDate: string
  grossAmount: number
  fees: number
  netCashFlow: number
  realizedPnl: number | null
  executedAtUtc: string
}

export interface CashEntry {
  id: string
  type: string
  amount: number
  balanceAfter: number
  timestampUtc: string
  description: string
}

export interface Order {
  id: string
  batchId: string
  proposalId: string
  symbol: string
  side: OrderSide
  requestedQuantity: number
  filledQuantity: number
  status: OrderStatus
  executionPrice: number | null
  createdAtUtc: string
  message: string | null
}

export interface Allocation {
  symbol: string
  name: string | null
  sector: string | null
  weight: number
  expectedReturn: number
  volatility: number
  riskContribution: number | null
}

export interface FrontierPoint {
  expectedReturn: number
  volatility: number
  sharpeRatio: number | null
  weights: Record<string, number>
}

export interface ReferencePortfolio {
  name: string
  expectedReturn: number | null
  volatility: number | null
  sharpeRatio: number | null
  note: string | null
}

export interface AssetStatistics {
  symbol: string
  expectedReturn: number
  volatility: number
  sharpeRatio: number | null
  droppedObservations: number
  usesAdjustedClose: boolean
}

export interface RunAnalytics {
  frontier: FrontierPoint[]
  correlationSymbols: string[]
  correlation: number[][]
  assetStatistics: AssetStatistics[]
  references: ReferencePortfolio[]
  inSample: { dates: string[]; cumulativeReturns: Record<string, number[]> } | null
  covarianceShrinkage: number
  excludedForData: string[]
}

export interface OptimizationRunSummary {
  id: string
  portfolioId: string
  createdAtUtc: string
  objective: ObjectiveKind
  status: RunStatus
  message: string
  expectedReturn: number | null
  volatility: number | null
  sharpeRatio: number | null
  assetCount: number
  periodStart: string | null
  periodEnd: string | null
  priceSource: string
  usesSimulatedData: boolean
}

export interface OptimizationRun extends Omit<OptimizationRunSummary, 'assetCount'> {
  symbols: string[]
  observations: number
  dataSnapshotHash: string
  settings: PortfolioSettings
  solverIterations: number
  solverCalls: number
  durationMs: number
  diagnostics: string[]
  allocation: Allocation[]
  analytics: RunAnalytics
  methodology: string
}

export interface ProposalLine {
  symbol: string
  companyName: string | null
  price: number | null
  priceDate: string | null
  priceIsStale: boolean
  targetWeight: number
  currentWeight: number
  resultingWeight: number
  currentQuantity: number
  targetValue: number
  targetQuantity: number
  orderQuantity: number
  action: ProposalAction
  estimatedTradeValue: number
  estimatedCost: number
  note: string | null
}

export interface TradeProposal {
  id: string
  portfolioId: string
  optimizationRunId: string | null
  createdAtUtc: string
  status: ProposalStatus
  allowFractionalShares: boolean
  portfolioValue: number
  cashBefore: number
  estimatedCashAfter: number
  estimatedCosts: number
  totalBuys: number
  totalSells: number
  targetCashWeight: number
  resultingCashWeight: number
  notes: string[]
  lines: ProposalLine[]
}

export interface PaperTradeResult {
  batchId: string
  proposalId: string
  executedAtUtc: string
  idempotentReplay: boolean
  totalFees: number
  cashAfter: number
  orders: Order[]
  notes: string[]
}

export interface PeriodReturn {
  year: number
  month: number
  portfolio?: number | null
  benchmark: number | null
}

export interface Performance {
  portfolioId: string
  benchmarkSymbol: string
  from: string | null
  to: string | null
  series: { date: string; value: number; cumulativeReturn: number; benchmarkCumulativeReturn: number | null; drawdown: number }[]
  totalReturn: Metric
  annualizedReturn: Metric
  annualizedVolatility: Metric
  sharpeRatio: Metric
  sortinoRatio: Metric
  maxDrawdown: Metric
  benchmarkReturn: Metric
  monthlyReturns: PeriodReturn[]
  methodology: string
  priceSource: string
  usesSimulatedData: boolean
  warnings: string[]
}

export interface Risk {
  portfolioId: string
  lookbackDays: number
  from: string | null
  to: string | null
  observations: number
  confidenceLevel: number
  annualizedVolatility: Metric
  valueAtRisk: Metric
  conditionalValueAtRisk: Metric
  beta: Metric
  trackingError: Metric
  informationRatio: Metric
  sharpeRatio: Metric
  maxDrawdown: Metric
  contributions: { symbol: string; weight: number; riskContribution: number | null; volatility: number }[]
  correlationSymbols: string[]
  correlation: number[][]
  methodology: string
  warnings: string[]
}

export interface BacktestRequest {
  name?: string
  universe: string[]
  startDate: string
  endDate: string
  initialCapital: number
  rebalanceFrequency: RebalanceKind
  objective: ObjectiveKind
  covarianceMethod: CovarianceKind
  lookbackDays: number
  transactionCostBps: number
  riskFreeRate: number
  benchmarkSymbol: string | null
  minWeight: number
  maxWeight: number
  cashReserve: number
  maxHoldings: number | null
  targetReturn: number | null
  maxVolatility: number | null
}

export interface BacktestSeriesMetrics {
  name: string
  totalReturn: number
  annualizedReturn: Metric
  annualizedVolatility: Metric
  sharpeRatio: Metric
  sortinoRatio: Metric
  maxDrawdown: Metric
  totalTurnover: number
  totalTransactionCosts: number
}

export interface BacktestResult {
  dates: string[]
  values: Record<string, number[]>
  drawdowns: Record<string, number[]>
  metrics: BacktestSeriesMetrics[]
  rebalances: {
    signalDate: string
    executionDate: string
    weights: Record<string, number>
    eligibleSymbols: string[]
    turnover: number
    transactionCosts: number
    portfolioValue: number
    optimizationSucceeded: boolean
    message: string
  }[]
  monthlyReturns: { year: number; month: number; portfolio: number | null; benchmark: number | null }[]
  beta: Metric
  trackingError: Metric
  informationRatio: Metric
  warnings: string[]
  methodology: string
}

export interface BacktestSummary {
  id: string
  name: string
  status: JobStatus
  createdAtUtc: string
  completedAtUtc: string | null
  priceSource: string
  usesSimulatedData: boolean
  error: string | null
}

export interface Backtest extends BacktestSummary {
  startedAtUtc: string | null
  config: BacktestRequest
  result: BacktestResult | null
}
