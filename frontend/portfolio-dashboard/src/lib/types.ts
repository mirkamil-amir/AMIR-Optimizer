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

// ---------------------------------------------------------------- identity & administration

export type UserAccountStatus = 'Pending' | 'Approved' | 'Rejected' | 'Suspended'

export interface CurrentUser {
  id: string
  email: string
  displayName: string
  status: UserAccountStatus
  roles: string[]
  isAdministrator: boolean
  createdAtUtc: string
}

export interface UserSummary {
  id: string
  email: string
  displayName: string
  status: UserAccountStatus
  roles: string[]
  createdAtUtc: string
  statusChangedAtUtc: string | null
  statusReason: string | null
  lastLoginAtUtc: string | null
  isLockedOut: boolean
  /** Automated-test account (reserved .test domain). */
  isTestAccount: boolean
}

export interface AdminOverview {
  accounts: { pending: number; approved: number; rejected: number; suspended: number; administrators: number; testAccounts: number }
  activity: {
    optimizationRuns: number
    optimizationRunsLast30Days: number
    mlExperiments: number
    mlExperimentsFailed: number
    mlExperimentsActive: number
    strategyComparisons: number
    backtests: number
    portfolios: number
  }
  recentRuns: { kind: string; id: string; name: string; status: string; ownerName: string; ownerEmail: string; createdAtUtc: string }[]
  marketData: {
    activeProvider: string
    providers: { name: string; displayName: string; isConfigured: boolean; requiresApiKey: boolean; isSimulated: boolean; rateLimit: string; remainingRequestsToday: number | null }[]
    indices: { code: string; name: string; members: number; withQuote: number; withQuoteToday: number; withHistory: number; verifiedOn: string | null }[]
    recentQuoteRefreshes: QuoteRefreshRun[]
    lastHistorySync: { id: string; status: string; requestedAtUtc: string; completedAtUtc: string | null; assetsTotal: number; assetsFailed: number; message: string | null } | null
  }
  generatedAtUtc: string
}

export interface AuditEvent {
  id: number
  timestampUtc: string
  actorUserId: string | null
  actorEmail: string | null
  action: string
  targetType: string | null
  targetId: string | null
  success: boolean
  details: string | null
  ipAddress: string | null
}

// ---------------------------------------------------------------- stock discovery

export type QuoteKind = 'None' | 'DelayedQuote' | 'LastClose'

export interface Sector {
  name: string
  stockCount: number
  withPriceData: number
}

export interface MarketIndex {
  code: string
  name: string
  country: string
  currency: string
  benchmarkSymbol: string | null
  isHistorical: boolean
  description: string | null
  members: number
  membersWithPriceHistory: number
  membersWithQuotes: number
  source: string
  sourceUrl: string | null
  sourceRevision: string | null
  licence: string | null
  verifiedOn: string | null
}

export interface Market {
  country: string
  name: string
  indices: MarketIndex[]
}

export interface QuoteRefreshRun {
  id: string
  trigger: string
  source: string
  status: JobStatus
  requestedAtUtc: string
  startedAtUtc: string | null
  completedAtUtc: string | null
  symbolsRequested: number
  updated: number
  unchanged: number
  failed: number
  message: string | null
  reused: boolean
}

export type QuoteRefreshOutcome = 'Updated' | 'Fresh' | 'Failed' | 'Unsupported' | 'NotFound'

export interface QuoteRefreshResult {
  items: { symbol: string; outcome: QuoteRefreshOutcome; message: string | null }[]
  stocks: StockListItem[]
}

export interface StockQuote {
  symbol: string
  price: number | null
  currency: string
  kind: QuoteKind
  priceDate: string | null
  asOfUtc: string | null
  previousClose: number | null
  previousCloseDate: string | null
  change: number | null
  changePercent: number | null
  provider: string | null
  retrievedAtUtc: string | null
  isSimulated: boolean
  isStale: boolean
  /** Retrieved longer ago than the server's cache window; a refresh may give a newer price. */
  isCached: boolean
  description: string
}

export interface StockListItem {
  id: number
  symbol: string
  name: string | null
  sector: string | null
  industry: string | null
  exchange: string | null
  /** ISO country of the listing venue. */
  country: string | null
  /** Index codes the stock belongs to (SP500, DAX40, …). */
  indices: string[]
  currency: string
  assetType: 'Equity' | 'Etf' | 'Index'
  isTracked: boolean
  observations: number
  firstDate: string | null
  lastDate: string | null
  quote: StockQuote
}

export interface StockDetail {
  stock: StockListItem
  metadataSource: string | null
  metadataUpdatedAtUtc: string | null
  hasAdjustedCloses: boolean
  dataSource: string
  isin: string | null
  mic: string | null
}

export interface CatalogueImportResult {
  rows: number
  inserted: number
  updated: number
  unchanged: number
  errors: string[]
}

// ---------------------------------------------------------------- machine learning

export type ModelFamily = 'RidgeRegression' | 'LogisticRegression' | 'RandomForest' | 'GradientBoosting'
export type PredictionTarget = 'ForwardReturn' | 'ForwardExcessReturn' | 'OutperformBenchmark' | 'ForwardVolatility'
export type WeightingMethod = 'EqualWeight' | 'RankWeighted' | 'InverseVolatility' | 'ScoreProportional'

export interface MlExperimentRequest {
  name?: string | null
  universe: string[]
  benchmarkSymbol: string
  modelFamily: ModelFamily
  target: PredictionTarget
  horizonDays: number
  historyYears: number
  features: string[]
  sampleStepDays: number
  trainFraction: number
  validationFraction: number
  embargoDays: number
  hyperparameters?: Record<string, number> | null
  seed: number
  topK: number
  weighting: WeightingMethod
  maxWeight: number
  cashReserve: number
  minimumScore?: number | null
  maxTurnover?: number | null
  sectorLimits: Record<string, number>
  rebalance: RebalanceKind
  retrainEveryRebalances: number
  trainingWindowDays?: number | null
  transactionCostBps: number
  initialCapital: number
  riskFreeRate: number
}

export interface NamedOption { value: string; label: string; description: string }

export interface MlCatalog {
  modelFamilies: { value: ModelFamily; label: string; description: string; supportedTargets: PredictionTarget[]; defaultSearchSpaceRegression: Record<string, number>[]; defaultSearchSpaceClassification: Record<string, number>[] }[]
  targets: NamedOption[]
  features: { name: string; group: string; definition: string; windowDays: number; requiresVolume: boolean; requiresBenchmark: boolean; requiresSectorPeers: boolean }[]
  featureSetVersion: string
  missingValuePolicy: string
  weightingMethods: NamedOption[]
  validationMethodology: string
  backtestMethodology: string
  maxUniverse: number
}

export interface MlExperimentSummary {
  id: string
  name: string
  status: JobStatus
  progressPercent: number
  progressMessage: string | null
  modelFamily: ModelFamily
  target: PredictionTarget
  horizonDays: number
  universeSize: number
  createdAtUtc: string
  completedAtUtc: string | null
  usesSimulatedData: boolean
  isExploratory: boolean
  testRankCorrelation: number | null
  testOutOfSampleR2: number | null
  testRocAuc: number | null
  backtestAnnualizedReturn: number | null
  error: string | null
}

export interface RegressionMetrics { count: number; mae: number; rmse: number; outOfSampleR2: number | null; pooledRankCorrelation: number | null; directionalAccuracy: number | null }
export interface CalibrationBin { meanPredicted: number; observedFrequency: number; count: number }
export interface ClassificationMetrics {
  count: number; baseRate: number; meanPredicted: number; rocAuc: number | null; brier: number; baseRateBrier: number; brierSkillScore: number | null
  logLoss: number; accuracy: number; precision: number | null; recall: number | null; calibration: CalibrationBin[]
}
export interface RankingMetrics {
  datesEvaluated: number; topK: number; meanInformationCoefficient: number | null; informationCoefficientStdDev: number | null
  informationCoefficientTStat: number | null; nonOverlappingDates: number; topKMeanOutcome: number | null; universeMeanOutcome: number | null
  topKExcessOverUniverse: number | null; topKHitRate: number | null; topMinusBottomSpread: number | null; rankStability: number | null
}
export interface PeriodMetrics { period: string; regression: RegressionMetrics | null; classification: ClassificationMetrics | null; ranking: RankingMetrics }
export interface PeriodInfo { start: string; end: string; samples: number; decisionDates: number; purged: number }
export interface MlResults {
  targetDefinition: string
  featureNames: string[]
  trials: { hyperparameters: Record<string, number>; validationLoss: number; validationRankCorrelation: number | null }[]
  selectedHyperparameters: Record<string, number>
  trainMetrics: PeriodMetrics
  validationMetrics: PeriodMetrics
  testMetrics: PeriodMetrics
  baselines: { name: string; description: string; regression: RegressionMetrics | null; classification: ClassificationMetrics | null; ranking: RankingMetrics | null }[]
  permutationImportance: { feature: string; meanLossIncrease: number; stdDev: number }[]
  nativeExplanation: { method: string; values: { name: string; value: number }[]; interpretation: string }
  contributionMethod: string
  trainPeriod: PeriodInfo
  validationPeriod: PeriodInfo
  testPeriod: PeriodInfo
  totalSamples: number
  droppedSamples: number
  validationMethodology: string
}
export interface MlModelVersion {
  id: string; version: number; role: string; modelFamily: string; trainedFrom: string; trainedThrough: string; trainingSamples: number
  hyperparameters: Record<string, number>; featureNames: string[]; featureSetVersion: string; artifactSha256: string; artifactSizeBytes: number; createdAtUtc: string
}
export interface FeatureContribution { feature: string; featureValue: number | null; contribution: number }
export interface MlPrediction {
  symbol: string; name: string | null; sector: string | null; latestPrice: number | null; latestPriceDate: string | null
  predictedValue: number; score: number; rank: number; selected: boolean; targetWeight: number
  intervalLow: number | null; intervalHigh: number | null; drivers: FeatureContribution[]
}
export interface MlPredictionSet {
  id: string; modelVersionId: string; modelVersion: number; modelRole: string; modelTrainedThrough: string; asOfDate: string; createdAtUtc: string
  horizonDays: number; cashWeight: number; intervalMethod: string | null; notes: string[]; usesSimulatedData: boolean; predictions: MlPrediction[]
}
export interface MlExperiment {
  summary: MlExperimentSummary
  configuration: MlExperimentRequest
  featureSetVersion: string
  benchmarkSymbol: string
  symbols: string[]
  priceSource: string
  historyStart: string
  historyEnd: string
  dataSnapshotHash: string | null
  startedAtUtc: string | null
  durationMs: number | null
  warnings: string[]
  results: MlResults | null
  modelVersions: MlModelVersion[]
  latestPredictions: MlPredictionSet | null
  disclaimer: string
}

export interface StrategyPerformance {
  name: string; totalReturn: number; annualizedReturn: Metric; annualizedVolatility: Metric; sharpeRatio: Metric; sortinoRatio: Metric
  maxDrawdown: Metric; historicalVaR95: Metric; expectedShortfall95: Metric; totalTurnover: number; totalTransactionCosts: number
  transactionCostsFraction: number; rebalances: number; averageHoldings: number; averageMaxWeight: number; averageEffectiveHoldings: number
  excessAnnualizedReturn: Metric; beta: Metric; trackingError: Metric; informationRatio: Metric
}
export interface MlRebalanceRecord {
  signalDate: string; modelTrainedThrough: string; modelTrainingSamples: number; retrained: boolean; selected: string[]
  targetWeights: Record<string, number>; notes: string[]
}
export interface MlBacktest {
  dates: string[]
  values: Record<string, number[]>
  drawdowns: Record<string, number[]>
  performance: StrategyPerformance[]
  rebalances: MlRebalanceRecord[]
  monthlyReturns: { series: string; year: number; month: number; return: number }[]
  warnings: string[]
  methodology: string
}

// ---------------------------------------------------------------- strategy comparison

export type StrategyKind = 'Mathematical' | 'MachineLearning' | 'Hybrid' | 'EqualWeight' | 'Benchmark'
export type ComparisonUniverse = 'Intersection' | 'Union'

export interface StrategyComparisonRequest {
  name?: string | null
  optimizationRunId: string
  mlExperimentId: string
  startDate?: string | null
  endDate?: string | null
  initialCapital: number
  benchmarkSymbol: string
  transactionCostBps: number
  rebalance: RebalanceKind
  universe: ComparisonUniverse
  includeHybrid: boolean
  riskFreeRate?: number | null
}

export interface ComparisonStrategy {
  key: string
  name: string
  kind: StrategyKind
  description: string
  values: number[]
  drawdowns: number[]
  rollingSharpe: (number | null)[]
  performance: StrategyPerformance
  monthlyReturns: { series: string; year: number; month: number; return: number }[]
  finalWeights: Record<string, number>
  averageWeights: Record<string, number>
  averageSectorExposure: Record<string, number>
  rebalances: { signalDate: string; executionDate: string; turnover: number; transactionCosts: number; holdings: number; succeeded: boolean; message: string }[]
}

export interface ComparisonResult {
  dates: string[]
  strategies: ComparisonStrategy[]
  overlaps: { strategyA: string; strategyB: string; commonHoldings: string[]; onlyA: string[]; onlyB: string[]; jaccardSimilarity: number; weightOverlap: number; averageWeightOverlap: number }[]
  universe: string[]
  universeNotes: string[]
  fairness: { isFair: boolean; sharedConventions: string[]; issues: string[] }
  rankingBySharpe: string[] | null
  rollingSharpeWindow: number
  methodology: string
  warnings: string[]
}

export interface StrategyComparisonSummary {
  id: string; name: string; status: JobStatus; progressPercent: number; progressMessage: string | null; createdAtUtc: string; completedAtUtc: string | null
  evaluationStart: string; evaluationEnd: string; optimizationRunId: string | null; mlExperimentId: string | null; usesSimulatedData: boolean
  isFair: boolean | null; error: string | null
}

export interface StrategyComparison {
  summary: StrategyComparisonSummary
  configuration: {
    request: StrategyComparisonRequest
    optimizationSettings: PortfolioSettings
    optimizationObjective: string
    optimizationRunCreatedAtUtc: string
    mlConfiguration: MlExperimentRequest
    mlHyperparameters: Record<string, number>
    mlFeatures: string[]
    mlValidationEnd: string | null
    mlTestStart: string | null
    mlFeatureSetVersion: string
  }
  benchmarkSymbol: string
  priceSource: string
  dataSnapshotHash: string | null
  warnings: string[]
  result: ComparisonResult | null
  disclaimer: string
}

// ---------------------------------------------------------------- watchlist & price history

export interface PricePoint {
  date: string
  price: number
}

export type HistoryRange = '1D' | '1M' | '3M' | '6M' | '1Y' | '5Y' | 'MAX'

/** Stored daily prices for a chart range. `change` is null (with `limitation`) when the stored history does not cover the range. */
export interface PriceHistory {
  symbol: string
  name: string | null
  currency: string
  exchange: string
  range: HistoryRange
  supported: boolean
  /** "Adjusted close" (splits and dividends) or "Close" (raw). */
  basis: string
  points: PricePoint[]
  baseDate: string | null
  basePrice: number | null
  endDate: string | null
  endPrice: number | null
  change: number | null
  availableFrom: string | null
  limitation: string | null
  source: string
  isSimulated: boolean
  lastRetrievedAtUtc: string | null
  supportedRanges: HistoryRange[]
}

export interface PeriodChange {
  range: HistoryRange
  change: number | null
  baseDate: string | null
  endDate: string | null
  basis: string
  limitation: string | null
  sparkline: PricePoint[]
}

export interface WatchlistItem {
  id: string
  addedAtUtc: string
  stock: StockListItem
  period: PeriodChange
}

export interface Watchlist {
  items: WatchlistItem[]
  range: HistoryRange
  maxEntries: number
  note: string
}

export interface WatchlistAddResult {
  item: WatchlistItem
  historyQueued: boolean
}

// ---------------------------------------------------------------- financial news

export type NewsCategory =
  | 'General' | 'Earnings' | 'CorporateTransaction' | 'RegulatoryLegal' | 'CentralBank' | 'Macroeconomic'
  | 'Geopolitical' | 'CommoditiesEnergy' | 'CurrenciesBonds' | 'MarketIndex'

export type NewsScope = 'Company' | 'Market' | 'Macro'

export interface NewsAsset {
  symbol: string
  name: string | null
  matchMethod: string
  onWatchlist: boolean
}

export interface NewsRelevance {
  level: 'Low' | 'Medium' | 'High'
  points: number
  reasons: string[]
  note: string
}

export interface NewsArticle {
  id: string
  headline: string
  publisher: string
  url: string
  publishedAtUtc: string
  retrievedAtUtc: string
  summary: string | null
  imageUrl: string | null
  imageCredit: string | null
  category: NewsCategory
  categoryBasis: string
  scope: NewsScope
  region: string | null
  isOfficialSource: boolean
  isSimulated: boolean
  provider: string
  assets: NewsAsset[]
  relevance: NewsRelevance
  alsoReportedBy: string[]
}

export interface NewsSourceStatus {
  key: string
  provider: string
  displayName: string
  isConfigured: boolean
  isOfficial: boolean
  isSimulated: boolean
  coverage: string
  terms: string
  lastAttemptUtc: string | null
  lastSuccessUtc: string | null
  lastArticleCount: number
  lastError: string | null
}

export interface NewsFeed {
  items: NewsArticle[]
  page: number
  pageSize: number
  totalCount: number
  sources: NewsSourceStatus[]
  imagesEnabled: boolean
  notes: string[]
}

export interface NewsFilters {
  category?: NewsCategory
  scope?: NewsScope
  region?: string
  symbol?: string
  watchlist?: boolean
  from?: string
  to?: string
  publisher?: string
  search?: string
  page?: number
  pageSize?: number
}

// ---------------------------------------------------------------- portfolio reality check

export interface RealityFinding {
  kind: 'Fact' | 'Estimate' | 'Interpretation' | 'Caution'
  topic: string
  text: string
  basis: string | null
}

export interface HistoricalCheck {
  label: string
  from: string
  to: string
  days: number
  cumulativeReturn: number
  annualizedVolatility: Metric
  maxDrawdown: Metric
  valueAtRisk95: Metric
  expectedShortfall95: Metric
  worstDay: number
  worst20DayReturn: number | null
  benchmarkSymbol: string | null
  benchmarkCumulativeReturn: number | null
  benchmarkMaxDrawdown: Metric | null
  beta: Metric | null
}

export interface SensitivityScenario {
  key: string
  name: string
  description: string
  status: string
  message: string | null
  turnover: number | null
  largestChange: number | null
  largestChangeSymbol: string | null
  holdings: number | null
  expectedReturn: number | null
  volatility: number | null
  weights: { symbol: string; weight: number; savedWeight: number }[]
}

export interface RealityCheck {
  runId: string
  objective: string
  runCreatedAtUtc: string
  currency: string
  windowStart: string
  windowEnd: string
  observations: number
  priceSource: string
  usesSimulatedData: boolean
  dataChangedSinceRun: boolean
  concentration: {
    holdings: number; largestWeight: number; largestSymbol: string; topThreeWeight: number; hhi: number; effectiveHoldings: number
    sectors: { sector: string; weight: number; holdings: number }[]
  }
  risk: {
    volatility: number; diversificationRatio: number; effectiveRiskContributors: number | null; averageCorrelation: number | null
    highestCorrelation: { a: string; b: string; correlation: number } | null
    averageCorrelationOnStressDays: number | null; stressDays: number
    contributions: { symbol: string; weight: number; riskShare: number; volatility: number }[]
  }
  inSample: HistoricalCheck
  outOfSample: HistoricalCheck | null
  outOfSampleUnavailableReason: string | null
  scenarios: SensitivityScenario[]
  costs: { costBps: number; buildCost: number; largestSwitchTurnover: number | null; switchCost: number | null; switchCostAtTripleBps: number | null; note: string }
  findings: RealityFinding[]
  nextSteps: string[]
  limitations: string[]
  methodology: string
  disclaimer: string
}

export interface EventHorizon {
  tradingDays: number
  date: string | null
  stockChange: number | null
  benchmarkChange: number | null
  difference: number | null
  limitation: string | null
}

export interface EventComparison {
  articleId: string
  headline: string
  symbol: string
  currency: string
  benchmarkSymbol: string | null
  basis: string
  source: string
  isSimulated: boolean
  publishedAtUtc: string
  publishedLocal: string
  timing: string
  referenceDate: string | null
  referencePrice: number | null
  horizons: EventHorizon[]
  overlappingArticles: NewsArticle[]
  limitations: string[]
  disclaimer: string
}

// ---------------------------------------------------------------- company profile

export type CompanyProfileStatus = 'Available' | 'NotFound' | 'NoReference' | 'Error' | 'NotConfigured'

/** Company description with its source; `description` is null unless `status` is Available (never invented). */
export interface CompanyProfile {
  symbol: string
  status: CompanyProfileStatus
  description: string | null
  sourceName: string | null
  sourceUrl: string | null
  licence: string | null
  language: string | null
  retrievedAtUtc: string | null
  /** An earlier description shown because the latest refresh failed. */
  isStale: boolean
  isSimulated: boolean
  message: string | null
}
