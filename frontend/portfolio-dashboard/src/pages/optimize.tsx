import { useMemo, useState, type ComponentProps, type ReactNode } from 'react'
import { Link, Navigate, useNavigate, useParams, useSearchParams } from 'react-router'
import { Controller, useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { toast } from 'sonner'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import { Switch } from '@/components/ui/switch'
import {
  AllocationDonut, ChartCard, CorrelationHeatmap, FrontierChart, RiskContributionChart, TimeSeriesChart, timeSeriesTable,
} from '@/components/charts'
import { DataTable, SearchableTable } from '@/components/data-table'
import { UniverseField } from '@/components/universe-field'
import { GroupedBarChart, groupedBarTable } from '@/components/analysis-charts'
import { drawdownFromCumulative, equalWeight, weightsBySector } from '@/lib/run-charts'
import { DataBadge, EmptyState, ErrorState, InfoTip, LoadingBlock, MetricCard, PageHeader, ResultSection } from '@/components/common'
import { TradeProposalView } from '@/components/proposal'
import { DataNotesList, StrategyInsights } from '@/components/strategy-insights'
import { interpretOptimization } from '@/lib/interpretation'
import { guideForObjective } from '@/lib/strategy-guide'
import { errorMessage } from '@/lib/api'
import { portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { useAssets, useCreateProposal, useFetchPrices, useMarketStatus, usePortfolio, useRun, useRunOptimization, useRuns, useStocks } from '@/lib/queries'
import { useMissingDataGuard } from '@/components/missing-data-dialog'
import { lookbackStart } from '@/lib/data-check'
import { useSelection } from '@/lib/selection'
import { CurrencyExposureList } from '@/components/currency'
import type { Allocation, Asset, ObjectiveKind, OptimizationRun, Portfolio, PortfolioSettings, TradeProposal } from '@/lib/types'
import { date, dateTime, num, objectiveLabels, pct } from '@/lib/format'
import { InvestableValueCard, NoPortfolio } from './shared'
import { MatrixLoader } from '@/components/matrix-loader'

// ------------------------------------------------------------------ form schema (percent units in the UI)

const optionalNumber = z.number().finite().optional()

/** Mirror of `OptimizationService.MaxUniverse` (the server enforces it). */
const MAX_UNIVERSE = 500

const schema = z.object({
  universe: z.array(z.string()).min(1, 'Select at least one asset')
    .max(MAX_UNIVERSE, `A Math Optimization can use at most ${MAX_UNIVERSE} stocks – narrow the universe by market, index or sector.`),
  lookbackDays: z.number().int().min(60).max(5040),
  objective: z.enum(['MinimumVariance', 'MaximumSharpe', 'TargetReturn', 'MaximumReturnForRisk', 'EqualWeight']),
  covarianceMethod: z.enum(['Sample', 'LedoitWolf']),
  riskFreeRate: z.number().min(-5).max(25),
  benchmarkSymbol: z.string().min(1),
  minWeight: z.number().min(0).max(100),
  maxWeight: z.number().min(1).max(100),
  maxHoldings: z.number().int().min(1).max(500).optional(),
  minPositionWeight: optionalNumber,
  cashReserve: z.number().min(0).max(99),
  maxTurnover: optionalNumber,
  targetReturn: optionalNumber,
  maxVolatility: optionalNumber,
  transactionCostBps: z.number().min(0).max(500),
  minimumFee: z.number().min(0).max(1000),
  allowFractionalShares: z.boolean(),
  rebalanceFrequency: z.enum(['None', 'Monthly', 'Quarterly']),
  rebalanceDriftThreshold: z.number().min(0).max(100),
  sectorLimits: z.record(z.string(), z.number().min(0).max(100).optional()),
}).superRefine((v, ctx) => {
  if (v.minWeight > v.maxWeight) ctx.addIssue({ code: 'custom', path: ['minWeight'], message: 'Must not exceed the maximum weight' })
  if (v.objective === 'TargetReturn' && v.targetReturn === undefined) ctx.addIssue({ code: 'custom', path: ['targetReturn'], message: 'Required for the target-return objective' })
  if (v.objective === 'MaximumReturnForRisk' && (v.maxVolatility === undefined || v.maxVolatility <= 0)) ctx.addIssue({ code: 'custom', path: ['maxVolatility'], message: 'Required for the risk-limit objective' })
  if (v.minPositionWeight !== undefined && v.minPositionWeight > v.maxWeight) ctx.addIssue({ code: 'custom', path: ['minPositionWeight'], message: 'Must not exceed the maximum weight' })
})

type FormValues = z.infer<typeof schema>

const toForm = (s: PortfolioSettings): FormValues => ({
  universe: s.universe,
  lookbackDays: s.lookbackDays,
  objective: s.objective,
  covarianceMethod: s.covarianceMethod,
  riskFreeRate: round(s.riskFreeRate * 100),
  benchmarkSymbol: s.benchmarkSymbol,
  minWeight: round(s.minWeight * 100),
  maxWeight: round(s.maxWeight * 100),
  maxHoldings: s.maxHoldings ?? undefined,
  minPositionWeight: s.minPositionWeight !== null ? round(s.minPositionWeight * 100) : undefined,
  cashReserve: round(s.cashReserve * 100),
  maxTurnover: s.maxTurnover !== null ? round(s.maxTurnover * 100) : undefined,
  targetReturn: s.targetReturn !== null ? round(s.targetReturn * 100) : undefined,
  maxVolatility: s.maxVolatility !== null ? round(s.maxVolatility * 100) : undefined,
  transactionCostBps: s.transactionCostBps,
  minimumFee: s.minimumFee,
  allowFractionalShares: s.allowFractionalShares,
  rebalanceFrequency: s.rebalanceFrequency,
  rebalanceDriftThreshold: round(s.rebalanceDriftThreshold * 100),
  sectorLimits: Object.fromEntries(Object.entries(s.sectorLimits).map(([k, v]) => [k, round(v * 100)])),
})

const fromForm = (v: FormValues, excluded: string[]): PortfolioSettings => ({
  universe: v.universe,
  lookbackDays: v.lookbackDays,
  objective: v.objective,
  covarianceMethod: v.covarianceMethod,
  riskFreeRate: v.riskFreeRate / 100,
  benchmarkSymbol: v.benchmarkSymbol,
  minWeight: v.minWeight / 100,
  maxWeight: v.maxWeight / 100,
  maxHoldings: v.maxHoldings ?? null,
  minPositionWeight: v.minPositionWeight !== undefined ? v.minPositionWeight / 100 : null,
  cashReserve: v.cashReserve / 100,
  maxTurnover: v.maxTurnover !== undefined ? v.maxTurnover / 100 : null,
  sectorLimits: Object.fromEntries(Object.entries(v.sectorLimits).filter(([, x]) => x !== undefined && Number.isFinite(x)).map(([k, x]) => [k, x! / 100])),
  excludedSymbols: excluded,
  targetReturn: v.targetReturn !== undefined ? v.targetReturn / 100 : null,
  maxVolatility: v.maxVolatility !== undefined ? v.maxVolatility / 100 : null,
  transactionCostBps: v.transactionCostBps,
  minimumFee: v.minimumFee,
  allowFractionalShares: v.allowFractionalShares,
  rebalanceFrequency: v.rebalanceFrequency,
  rebalanceDriftThreshold: v.rebalanceDriftThreshold / 100,
})

function round(x: number) {
  return Math.round(x * 1e6) / 1e6
}

const num_ = { setValueAs: (v: unknown) => (v === '' || v === null || v === undefined ? undefined : Number(v)) }

const lookbacks = [
  { value: 252, label: '1 year (252 days)' },
  { value: 504, label: '2 years (504 days)' },
  { value: 756, label: '3 years (756 days)' },
  { value: 1260, label: '5 years (1,260 days)' },
]

// ------------------------------------------------------------------ page

export function OptimizePage() {
  const { runId } = useParams()
  const { portfolioId, isLoading } = usePortfolioContext()
  const runs = useRuns(portfolioId)
  const activeRunId = runId ?? runs.data?.items[0]?.id
  const run = useRun(activeRunId)

  if (!isLoading && !portfolioId) return <NoPortfolio />
  // A run always opens inside its own portfolio (a link with another portfolio's id must not mix the two).
  if (portfolioId && run.data && run.data.portfolioId !== portfolioId) return <Navigate to={portfolioPath(run.data.portfolioId, `optimize/${run.data.id}`)} replace />

  return (
    <>
      <PageHeader
        level={2}
        titleHidden
        title="Math Optimization"
        info="Configure the universe and constraints of this portfolio, run a mean-variance optimization, then review its allocation and confirm a simulated trade proposal. A result never changes holdings by itself."
      />
      <div className="grid gap-6 xl:grid-cols-[400px_minmax(0,1fr)]">
        <div className="space-y-4">
          {/* History first: picking an earlier run is the first thing many visits do. */}
          <RunHistory activeId={activeRunId} />
          {portfolioId && <ConfigForm portfolioId={portfolioId} />}
        </div>
        <div className="min-w-0 scroll-mt-4" id="optimization-results">
          {run.isLoading || runs.isLoading ? <LoadingBlock height="h-96" /> : run.error ? <ErrorState error={run.error} /> : run.data ? (
            <RunResults run={run.data} />
          ) : (
            <EmptyState title="No optimization yet" description="Configure the inputs on the left and click Solve." />
          )}
        </div>
      </div>
    </>
  )
}

function ConfigForm({ portfolioId }: { portfolioId: string }) {
  const portfolio = usePortfolio(portfolioId)
  const assets = useAssets()
  if (!portfolio.data || !assets.data) return <LoadingBlock height="h-[640px]" />
  // Mount the form only once its data exists so every input is controlled from the first render.
  return <ConfigFormInner key={portfolio.data.id} portfolio={portfolio.data} assets={assets.data} />
}

function ConfigFormInner({ portfolio: portfolioData, assets: assetList }: { portfolio: Portfolio; assets: Asset[] }) {
  const portfolioId = portfolioData.id
  const portfolio = { data: portfolioData }
  const assets = { data: assetList }
  const status = useMarketStatus()
  const runMutation = useRunOptimization(portfolioId)
  const navigate = useNavigate()
  const selection = useSelection()
  const [searchParams] = useSearchParams()
  const fromSelection = searchParams.get('from') === 'selection' && selection.count > 0
  // Saving settings as the portfolio's defaults is opt-in: a run always stores its own inputs, the selection is not stored otherwise.
  const [persistSettings, setPersistSettings] = useState(false)
  const fetchPrices = useFetchPrices()

  const missingData = useMissingDataGuard()
  const form = useForm<FormValues>({
    resolver: zodResolver(schema),
    mode: 'onBlur',
    defaultValues: { ...toForm(portfolioData.settings), ...(fromSelection ? { universe: selection.symbols } : {}) },
  })
  const { register, control, handleSubmit, watch, setValue, formState: { errors } } = form

  const objective = watch('objective')
  const watchedUniverse = watch('universe') ?? []
  const benchmark = watch('benchmarkSymbol')
  const coverage = useMemo(() => new Map((status.data?.coverage ?? []).map((c) => [c.symbol, c])), [status.data])
  const enabledAssets = (assets.data ?? []).filter((a) => a.isEnabled)
  const assetBySymbol = useMemo(() => new Map((assets.data ?? []).map((a) => [a.symbol, a])), [assets.data])
  const universeStocks = useStocks({ symbols: watchedUniverse, pageSize: 200 }, watchedUniverse.length > 0)
  const insufficient = (universeStocks.data?.items ?? []).filter((s) => s.observations < 61).map((s) => s.symbol)
  const sectors = [...new Set(watchedUniverse.map((s) => assetBySymbol.get(s)?.sector).filter((x): x is string => !!x))].sort()

  // Missing prices or exchange rates for the lookback window are offered for download first.
  const onSubmit = handleSubmit((values) => missingData.guard({
    symbols: values.universe, benchmarkSymbol: values.benchmarkSymbol || null, from: lookbackStart(new Date(), values.lookbackDays),
    currency: portfolio.data.baseCurrency,
  }, () => run(values)))
  const run = async (values: FormValues) => {
    try {
      const result = await runMutation.mutateAsync({
        settings: fromForm(values, portfolio.data.settings.excludedSymbols.filter((s) => values.universe.includes(s))),
        persistSettings,
      })
      if (result.status === 'Succeeded') toast.success('Optimization succeeded', { description: `Expected return ${pct(result.expectedReturn)} · volatility ${pct(result.volatility)}` })
      else toast.warning(`Optimization ${result.status.toLowerCase()}`, { description: result.message })
      navigate(portfolioPath(portfolioId, `optimize/${result.id}`))
    } catch (e) {
      toast.error('Optimization request failed', { description: errorMessage(e) })
    }
  }

  return (
    <Card>
      <CardContent>
        <form onSubmit={onSubmit} className="space-y-5" noValidate>
          <div className="space-y-2">
            <Controller
              control={control}
              name="universe"
              render={({ field }) => (
                <UniverseField symbols={field.value ?? []} onChange={(v) => setValue('universe', v, { shouldDirty: true, shouldValidate: true })}
                  browseTo={portfolioPath(portfolioId, 'optimize/stocks')} />
              )}
            />
            {errors.universe && <FieldError msg={errors.universe.message} />}
            {insufficient.length > 0 && (
              <div className="space-y-1.5 rounded-md border border-warn/40 p-2 text-xs">
                <p className="text-warn">{insufficient.join(', ')}: fewer than 61 stored prices – excluded from the optimization until prices are fetched.</p>
                <Button
                  type="button"
                  size="xs"
                  variant="outline"
                  disabled={fetchPrices.isPending}
                  onClick={async () => {
                    try {
                      const job = await fetchPrices.mutateAsync(insufficient.slice(0, 50))
                      if (job.assetsFailed === 0) toast.success('Price history downloaded', { description: `${job.assetsCompleted} stock(s). You can run the optimization now.` })
                      else toast.warning(`Price history: ${job.assetsCompleted} downloaded, ${job.assetsFailed} failed`, { description: job.message ?? undefined })
                      setTimeout(() => universeStocks.refetch(), 5000)
                    } catch (e) {
                      toast.error('Could not queue the download', { description: errorMessage(e) })
                    }
                  }}
                >
                  {fetchPrices.isPending && <MatrixLoader />} Fetch prices
                </Button>
              </div>
            )}
          </div>

          <div className="grid grid-cols-2 gap-3">
            <Field label="Lookback period" hint="Trailing window of daily returns used to estimate expected returns and covariances.">
              <Controller control={control} name="lookbackDays" render={({ field }) => (
                <Select value={field.value} onValueChange={(v) => field.onChange(Number(v))} items={lookbacks}>
                  <SelectTrigger className="w-full" aria-label="Lookback period"><SelectValue /></SelectTrigger>
                  <SelectContent>{lookbacks.map((l) => <SelectItem key={l.value} value={l.value}>{l.label}</SelectItem>)}</SelectContent>
                </Select>
              )} />
            </Field>
            <Field label="Covariance" hint="Ledoit–Wolf shrinkage stabilizes the covariance matrix for small samples; Sample uses the plain estimator.">
              <Controller control={control} name="covarianceMethod" render={({ field }) => (
                <Select value={field.value} onValueChange={(v) => field.onChange(v)} items={[{ value: 'LedoitWolf', label: 'Ledoit–Wolf' }, { value: 'Sample', label: 'Sample' }]}>
                  <SelectTrigger className="w-full" aria-label="Covariance estimator"><SelectValue /></SelectTrigger>
                  <SelectContent><SelectItem value="LedoitWolf">Ledoit–Wolf</SelectItem><SelectItem value="Sample">Sample</SelectItem></SelectContent>
                </Select>
              )} />
            </Field>
          </div>

          <Field label="Optimization method">
            <Controller control={control} name="objective" render={({ field }) => (
              <Select value={field.value} onValueChange={(v) => field.onChange(v as ObjectiveKind)} items={Object.entries(objectiveLabels).map(([value, label]) => ({ value, label }))}>
                <SelectTrigger className="w-full" aria-label="Optimization method"><SelectValue /></SelectTrigger>
                <SelectContent>{Object.entries(objectiveLabels).map(([k, l]) => <SelectItem key={k} value={k}>{l}</SelectItem>)}</SelectContent>
              </Select>
            )} />
          </Field>
          {objective === 'TargetReturn' && (
            <NumField label="Target expected return (% p.a.)" error={errors.targetReturn?.message} {...register('targetReturn', num_)} step="0.5" />
          )}
          {objective === 'MaximumReturnForRisk' && (
            <NumField label="Volatility limit (% p.a.)" error={errors.maxVolatility?.message} {...register('maxVolatility', num_)} step="0.5" />
          )}

          <Separator />
          <div className="grid grid-cols-2 gap-3">
            <NumField label="Max weight per asset (%)" error={errors.maxWeight?.message} {...register('maxWeight', num_)} />
            <NumField label="Min weight per asset (%)" error={errors.minWeight?.message} {...register('minWeight', num_)} hint="Applied to every asset in the universe (forces inclusion if > 0)." />
            <NumField label="Max number of holdings" error={errors.maxHoldings?.message} {...register('maxHoldings', num_)} placeholder="No limit" hint="Heuristic: the smallest positions are removed and the problem re-solved until the limit is met." />
            <NumField label="Min position size (%)" error={errors.minPositionWeight?.message} {...register('minPositionWeight', num_)} placeholder="None" hint="Positions smaller than this are removed (heuristic) rather than held as dust." />
            <NumField label="Cash reserve (%)" error={errors.cashReserve?.message} {...register('cashReserve', num_)} />
            <NumField label="Max turnover (%)" error={errors.maxTurnover?.message} {...register('maxTurnover', num_)} placeholder="No limit" hint="One-way turnover ½Σ|w − w₀| versus current holdings. Ignored when the portfolio holds only cash." />
          </div>

          {sectors.length > 0 && (
            <details className="rounded-md border px-3 py-2">
              <summary className="cursor-pointer text-sm font-medium">Sector limits (optional)</summary>
              <div className="mt-2 grid grid-cols-2 gap-2">
                {sectors.map((s) => (
                  <NumField key={s} label={`${s} max (%)`} {...register(`sectorLimits.${s}` as const, num_)} placeholder="No limit" />
                ))}
              </div>
            </details>
          )}

          <Separator />
          <div className="grid grid-cols-2 gap-3">
            <NumField label="Risk-free rate (% p.a.)" error={errors.riskFreeRate?.message} {...register('riskFreeRate', num_)} step="0.05" hint="Assumption used for Sharpe ratios – not observed market data." />
            <Field label="Benchmark">
              <Controller control={control} name="benchmarkSymbol" render={({ field }) => (
                <Select value={field.value} onValueChange={(v) => field.onChange(String(v))} items={enabledAssets.map((a) => ({ value: a.symbol, label: a.symbol }))}>
                  <SelectTrigger className="w-full" aria-label="Benchmark"><SelectValue /></SelectTrigger>
                  <SelectContent>{enabledAssets.map((a) => <SelectItem key={a.symbol} value={a.symbol}>{a.symbol}</SelectItem>)}</SelectContent>
                </Select>
              )} />
            </Field>
            <NumField label="Transaction cost (bps)" error={errors.transactionCostBps?.message} {...register('transactionCostBps', num_)} />
            <NumField label="Minimum fee per order" error={errors.minimumFee?.message} {...register('minimumFee', num_)} step="0.5" />
            <Field label="Rebalancing">
              <Controller control={control} name="rebalanceFrequency" render={({ field }) => (
                <Select value={field.value} onValueChange={(v) => field.onChange(v)} items={[{ value: 'None', label: 'Manual' }, { value: 'Monthly', label: 'Monthly' }, { value: 'Quarterly', label: 'Quarterly' }]}>
                  <SelectTrigger className="w-full" aria-label="Rebalancing frequency"><SelectValue /></SelectTrigger>
                  <SelectContent><SelectItem value="None">Manual</SelectItem><SelectItem value="Monthly">Monthly</SelectItem><SelectItem value="Quarterly">Quarterly</SelectItem></SelectContent>
                </Select>
              )} />
            </Field>
            <NumField label="Drift threshold (%)" {...register('rebalanceDriftThreshold', num_)} step="0.5" hint="During rebalancing, positions within this weight drift of target are not traded." />
          </div>
          <div className="flex items-center gap-2">
            <Controller control={control} name="allowFractionalShares" render={({ field }) => (
              <Switch id="fractional" checked={field.value} onCheckedChange={field.onChange} />
            )} />
            <Label htmlFor="fractional" className="font-normal">Allow fractional shares</Label>
          </div>
          <div className="flex items-center gap-2">
            <Switch id="persist" checked={persistSettings} onCheckedChange={setPersistSettings} />
            <Label htmlFor="persist" className="font-normal">Save these settings as the portfolio's defaults</Label>
            <InfoTip>Every run stores its own inputs. Turn this on to also make this universe and these constraints the portfolio's saved configuration.</InfoTip>
          </div>
          {benchmark && !watchedUniverse.includes(benchmark) && coverage.get(benchmark)?.freshness === 'Missing' && (
            <p className="text-xs text-warn">The benchmark has no price data – synchronize it in Atlas (Market Data).</p>
          )}

          <Button type="submit" className="w-full" disabled={runMutation.isPending || missingData.checking}>
            {(runMutation.isPending || missingData.checking) && <MatrixLoader />}
            {runMutation.isPending ? 'Solving…' : 'Solve'}
          </Button>
          <p className="text-[11px] text-muted-foreground">Saves these settings to the portfolio and stores the run for reproducibility.</p>
        </form>
        {missingData.dialog}
      </CardContent>
    </Card>
  )
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <div className="space-y-1.5">
      <Label className="flex items-center gap-1 text-xs"><InfoTip of={label}>{hint}</InfoTip></Label>
      {children}
    </div>
  )
}

function NumField({ label, hint, error, ...props }: { label: string; hint?: string; error?: string } & ComponentProps<'input'>) {
  const id = `f-${props.name}`
  return (
    <div className="space-y-1.5">
      <Label htmlFor={id} className="flex items-center gap-1 text-xs"><InfoTip of={label}>{hint}</InfoTip></Label>
      <Input id={id} type="number" inputMode="decimal" step="any" aria-invalid={!!error} aria-describedby={error ? `${id}-err` : undefined} {...props} />
      {error && <FieldError id={`${id}-err`} msg={error} />}
    </div>
  )
}

function FieldError({ msg, id }: { msg?: string; id?: string }) {
  return <p id={id} role="alert" className="text-xs text-destructive">{msg}</p>
}

function RunHistory({ activeId }: { activeId?: string }) {
  const { portfolioId } = usePortfolioContext()
  const runs = useRuns(portfolioId)
  if (!runs.data || runs.data.items.length === 0) return null
  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between">
        <CardTitle className="text-sm">Optimization history</CardTitle>
        <Badge variant="secondary" className="tabular">{runs.data.items.length}</Badge>
      </CardHeader>
      <CardContent className="max-h-64 space-y-1 overflow-y-auto">
        {runs.data.items.map((r) => (
          <Link key={r.id} to={portfolioPath(r.portfolioId, `optimize/${r.id}`)}
            // The results change on the right (below on narrow screens): bring them into view.
            onClick={() => requestAnimationFrame(() => document.getElementById('optimization-results')?.scrollIntoView({ behavior: 'smooth', block: 'start' }))}
            className={`block rounded-md border-l-2 px-2.5 py-1.5 text-sm hover:bg-muted ${r.id === activeId ? 'border-primary bg-muted' : 'border-transparent'}`} aria-current={r.id === activeId ? 'true' : undefined}>
            <div className="flex items-center justify-between">
              <span className="font-medium">{objectiveLabels[r.objective]}</span>
              <Badge variant={r.status === 'Succeeded' ? 'secondary' : 'destructive'} className="text-[10px]">{r.status}</Badge>
            </div>
            <div className="text-xs text-muted-foreground tabular">
              {dateTime(r.createdAtUtc)}
              {r.status === 'Succeeded' && ` · E[r] ${pct(r.expectedReturn, 1)} · σ ${pct(r.volatility, 1)} · SR ${num(r.sharpeRatio)}`}
            </div>
          </Link>
        ))}
      </CardContent>
    </Card>
  )
}

// ------------------------------------------------------------------ results

function RunResults({ run }: { run: OptimizationRun }) {
  // The run's own analysis currency (its portfolio's base currency at the time) – never another portfolio's.
  const ccy = run.currency
  const createProposal = useCreateProposal()
  // Proposal is tied to the run it was created for; switching runs hides it without an effect.
  const [created, setCreated] = useState<{ runId: string; proposal: TradeProposal } | null>(null)
  const proposal = created?.runId === run.id ? created.proposal : null

  const ok = run.status === 'Succeeded'
  const a = run.analytics
  const allocation = run.allocation.filter((x) => x.weight > 1e-6)
  const cashWeight = 1 - run.allocation.reduce((s, x) => s + x.weight, 0)
  // More views of the same stored run (reshaped, no new estimates).
  const sectors = weightsBySector(run.allocation)
  const ew = equalWeight(run.allocation.length, 1 - cashWeight)
  const vsEqual = [
    { name: 'Optimized', color: 'var(--series-1)', values: run.allocation.map((x) => x.weight) },
    { name: 'Equal weight', color: 'var(--series-3)', values: run.allocation.map(() => ew) },
  ]
  const stockStats = [
    { name: 'Expected return', color: 'var(--series-1)', values: a.assetStatistics.map((x) => x.expectedReturn) },
    { name: 'Volatility', color: 'var(--series-2)', values: a.assetStatistics.map((x) => x.volatility) },
  ]
  const dataNotes = [...(a.excludedForData.length ? [`Excluded for missing or too short price history: ${a.excludedForData.join(', ')}.`] : []), ...run.diagnostics]
  const inSampleDrawdowns = Object.entries(a.inSample?.cumulativeReturns ?? {}).map(([name, values]) => ({ name, values: drawdownFromCumulative(values) }))

  async function makeProposal() {
    try {
      setCreated({ runId: run.id, proposal: await createProposal.mutateAsync(run.id) })
    } catch (e) {
      toast.error('Could not create trade proposal', { description: errorMessage(e) })
    }
  }

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        {/* Like ML Optimization: the status only when it is not a success; currency and date on the right. */}
        <h2 className="text-lg font-semibold">{objectiveLabels[run.objective]}</h2>
        {!ok && <Badge variant="destructive">{run.status}</Badge>}
        {run.usesSimulatedData && <DataBadge kind="simulated" />}
        <div className="ml-auto flex flex-wrap items-center gap-2">
          <Badge variant="outline" title={run.fxSource ? `Prices in other currencies converted to ${run.currency} with the reference rate of each date` : `All prices in ${run.currency}`}>
            In {run.currency}{run.fxSource ? ' · FX converted' : ''}
          </Badge>
          <span className="text-xs text-muted-foreground tabular">{dateTime(run.createdAtUtc)}</span>
          {ok && (
            <Button size="sm" variant="outline" render={<Link to={`/reality-check/${run.id}`} />}>
              Portfolio reality check
            </Button>
          )}
        </div>
      </div>

      {!ok && (
        <Alert variant="destructive">
          <AlertTitle>{run.status === 'Infeasible' ? 'The constraints cannot all be satisfied' : 'Optimization did not produce an allocation'}</AlertTitle>
          <AlertDescription>
            <p>{run.message}</p>
            {run.diagnostics.length > 0 && <ul className="mt-2 list-disc pl-4">{run.diagnostics.map((d) => <li key={d}>{d}</li>)}</ul>}
          </AlertDescription>
        </Alert>
      )}

      {ok && (
        <div className="grid grid-cols-2 gap-3 md:grid-cols-3 2xl:grid-cols-5">
          <InvestableValueCard portfolioId={run.portfolioId} />
          <MetricCard label="Holdings · cash" value={`${allocation.length} · ${pct(cashWeight, 1)}`} sub={`${date(run.periodStart)} – ${date(run.periodEnd)} · ${run.observations} days`} />
          <MetricCard label="Expected return" value={pct(run.expectedReturn)} sub="Annualized historical mean" hint="Arithmetic mean of daily returns × 252 over the estimation window. A historical estimate, not a forecast." />
          <MetricCard label="Volatility" value={pct(run.volatility)} sub="Annualized" hint="sqrt(wᵀΣw) with Σ the annualized covariance matrix." />
          <MetricCard label="Sharpe ratio" value={num(run.sharpeRatio)} sub={`rf = ${pct(run.settings.riskFreeRate)}`} hint="(Expected return − risk-free rate) / volatility." />
        </div>
      )}

      {/* Next step right under the key figures: turn the weights into orders. */}
      {ok && (
        <Card>
          <CardHeader className="flex flex-row items-center justify-between gap-2">
            <div>
              <CardTitle className="flex items-center gap-2 text-sm">Trade proposal</CardTitle>
              <CardDescription className="text-xs">Translate the target weights into whole-share orders at the latest stored prices, after costs.</CardDescription>
            </div>
            <Button variant={proposal ? 'outline' : 'default'} onClick={makeProposal} disabled={createProposal.isPending}>
              {createProposal.isPending && <MatrixLoader />}
              {proposal ? 'Recalculate' : 'Create trade proposal'}
            </Button>
          </CardHeader>
          {proposal && (
            <CardContent>
              <TradeProposalView proposal={proposal} currency={ccy} />
            </CardContent>
          )}
        </Card>
      )}

      {/* Every result is visible at once (no tabs); the same section order as ML Optimization. */}
      {ok && (
        <ResultSection title="Allocation">
          <Card>
            <CardContent className="pt-2">
              <AllocationDonut items={[...allocation.map((x) => ({ name: x.symbol, value: x.weight })), ...(cashWeight > 1e-6 ? [{ name: 'Cash', value: cashWeight }] : [])]} />
            </CardContent>
          </Card>
          <AllocationTable rows={run.allocation} currency={run.currency} />
          <div className="grid gap-4 lg:grid-cols-2">
            <ChartCard title="Weight vs equal weight" description={`Each stock's optimized weight next to 1/${run.allocation.length} of the invested share`}
              legend={[{ name: 'Optimized', color: 'var(--series-1)' }, { name: 'Equal weight', color: 'var(--series-3)' }]}
              table={groupedBarTable(run.allocation.map((x) => x.symbol), vsEqual, (v) => pct(v, 1))}>
              <GroupedBarChart categories={run.allocation.map((x) => x.symbol)} series={vsEqual} format={(v) => pct(v, 0)} legend={false} />
            </ChartCard>
            <ChartCard title="Weight by sector" description="Sum of the optimized weights per sector (cash excluded)"
              table={{ columns: ['Sector', 'Weight'], rows: sectors.map((x) => [x.sector, pct(x.weight, 1)]) }}>
              <GroupedBarChart categories={sectors.map((x) => x.sector)} series={[{ name: 'Weight', color: 'var(--series-1)', values: sectors.map((x) => x.weight) }]} format={(v) => pct(v, 0)} legend={false} />
            </ChartCard>
          </div>
          {run.currencyExposure && run.currencyExposure.length > 1 && (
            <Card>
              <CardContent className="space-y-2 pt-2">
                <h3 className="text-sm font-semibold">Currency exposure</h3>
                <CurrencyExposureList items={run.currencyExposure} />
                <p className="text-xs text-muted-foreground">
                  Share of the allocation by trading currency. Returns and risk above are measured in {run.currency} and include these currencies&apos; movements against {run.currency} (unhedged).
                </p>
              </CardContent>
            </Card>
          )}
        </ResultSection>
      )}

      {a.frontier.length > 0 && (
        <ResultSection title="Efficient frontier">
          <ChartCard
            title="Efficient frontier"
            description={`Minimum-variance portfolios for ${a.frontier.length} target returns under the same constraints · annualized, ${date(run.periodStart)} – ${date(run.periodEnd)}`}
            hint="Each point is the lowest-volatility portfolio for a given expected return. Grey dots are the individual assets; squares are reference portfolios."
            legend={[{ name: 'Efficient frontier', color: 'var(--series-1)' }, { name: 'Selected portfolio', color: 'var(--series-2)' }, { name: 'Reference portfolios', color: 'var(--series-3)' }, { name: 'Individual assets', color: 'var(--chart-axis)' }]}
            table={{ columns: ['Expected return', 'Volatility', 'Sharpe'], rows: a.frontier.map((p) => [pct(p.expectedReturn), pct(p.volatility), num(p.sharpeRatio)]) }}
          >
            <FrontierChart
              frontier={a.frontier}
              assets={a.assetStatistics.map((s) => ({ symbol: s.symbol, volatility: s.volatility, expectedReturn: s.expectedReturn }))}
              selected={ok ? { volatility: run.volatility!, expectedReturn: run.expectedReturn!, sharpeRatio: run.sharpeRatio } : undefined}
              references={a.references}
            />
          </ChartCard>
        </ResultSection>
      )}

      {a.assetStatistics.length > 0 && (
        <ResultSection title="Return and risk of each stock">
          <ChartCard title="Expected return and volatility per stock"
            description={`Annualized, estimation window ${date(run.periodStart)} – ${date(run.periodEnd)} · in-sample estimates, not forecasts`}
            hint="The inputs the optimizer worked with: each stock's historical mean return and volatility over the estimation window."
            legend={[{ name: 'Expected return', color: 'var(--series-1)' }, { name: 'Volatility', color: 'var(--series-2)' }]}
            table={groupedBarTable(a.assetStatistics.map((x) => x.symbol), stockStats, (v) => pct(v, 1))}>
            <GroupedBarChart categories={a.assetStatistics.map((x) => x.symbol)} series={stockStats} format={(v) => pct(v, 0)} zeroLine legend={false} />
          </ChartCard>
        </ResultSection>
      )}

      {a.correlationSymbols.length > 0 && (
        <ResultSection title="Correlation">
          <ChartCard title="Correlation of daily returns" description={`Estimation window ${date(run.periodStart)} – ${date(run.periodEnd)}`}
            table={{ columns: ['', ...a.correlationSymbols], rows: a.correlation.map((r, i) => [a.correlationSymbols[i], ...r.map((v) => v.toFixed(3))]) }}>
            <CorrelationHeatmap symbols={a.correlationSymbols} matrix={a.correlation} />
          </ChartCard>
        </ResultSection>
      )}

      {ok && (
        <ResultSection title="Risk contribution">
          <ChartCard
            title="Weight vs. contribution to volatility"
            description="Share of portfolio variance attributable to each holding: wᵢ(Σw)ᵢ / wᵀΣw"
            legend={[{ name: 'Weight', color: 'var(--series-1)' }, { name: 'Risk contribution', color: 'var(--series-2)' }]}
            table={{ columns: ['Symbol', 'Weight', 'Risk contribution'], rows: allocation.map((x) => [x.symbol, pct(x.weight), pct(x.riskContribution)]) }}
          >
            <RiskContributionChart items={allocation.map((x) => ({ symbol: x.symbol, weight: x.weight, risk: x.riskContribution }))} />
          </ChartCard>
        </ResultSection>
      )}

      {a.references.length > 0 && (
        <ResultSection title="Comparison with reference portfolios">
          <DataTable
            caption="Comparison with reference portfolios"
            rows={[
              ...(ok ? [{ name: 'Optimized', expectedReturn: run.expectedReturn, volatility: run.volatility, sharpeRatio: run.sharpeRatio, note: objectiveLabels[run.objective] }] : []),
              ...a.references,
            ]}
            rowKey={(r) => r.name}
            columns={[
              { key: 'n', header: 'Portfolio', cell: (r) => <span className="font-medium">{r.name}</span> },
              { key: 'r', header: 'Expected return', align: 'right', cell: (r) => pct(r.expectedReturn) },
              { key: 'v', header: 'Volatility', align: 'right', cell: (r) => pct(r.volatility) },
              { key: 's', header: 'Sharpe', align: 'right', cell: (r) => num(r.sharpeRatio) },
              { key: 'note', header: 'Note', cell: (r) => <span className="text-xs text-muted-foreground">{r.note}</span> },
            ]}
          />
          {a.inSample && (
            <ChartCard
              title="In-sample cumulative return"
              description="Constant weights applied to the estimation window. In-sample results are optimistic by construction – this is not a backtest or forecast."
              legend={Object.keys(a.inSample.cumulativeReturns).map((k, i) => ({ name: k, color: `var(--series-${i + 1})` }))}
              table={timeSeriesTable(a.inSample.dates, Object.entries(a.inSample.cumulativeReturns).map(([name, values]) => ({ name, values })), (v) => pct(v))}
            >
              <TimeSeriesChart
                dates={a.inSample.dates}
                series={Object.entries(a.inSample.cumulativeReturns).map(([name, values]) => ({ name, values }))}
                format={(v) => pct(v, 0)}
                zeroLine
              />
            </ChartCard>
          )}
          {a.inSample && (
            <ChartCard
              title="In-sample drawdown"
              description="Decline from the running peak of each curve above, same window and same caveat: in-sample, not a backtest."
              legend={Object.keys(a.inSample.cumulativeReturns).map((k, i) => ({ name: k, color: `var(--series-${i + 1})` }))}
              table={timeSeriesTable(a.inSample.dates, inSampleDrawdowns, (v) => pct(v))}
            >
              <TimeSeriesChart dates={a.inSample.dates} series={inSampleDrawdowns} format={(v) => pct(v, 0)} yDomain={['dataMin', 0]} height={220} />
            </ChartCard>
          )}
        </ResultSection>
      )}

      {/* One reference box, last: what the numbers mean → how the run was computed → notes about its data. */}
      <StrategyInsights insights={interpretOptimization(run)} guideId={guideForObjective(run.objective)} sections={[
        { title: 'Inputs & methodology', content: (
          <div className="space-y-4">
            <p className="leading-relaxed text-muted-foreground">{run.methodology}</p>
            <dl className="grid grid-cols-1 gap-x-6 gap-y-2 sm:grid-cols-2">
              <Def k="Run ID" v={<code className="text-xs">{run.id}</code>} />
              <Def k="Prices" v={run.usesSimulatedData ? 'Simulated' : 'Stored daily closes'} />
              <Def k="Analysis currency" v={run.fxSource ? `${run.currency} – other currencies converted with the reference rate of each date` : `${run.currency} (no conversion needed)`} />
              <Def k="Estimation window" v={`${date(run.periodStart)} – ${date(run.periodEnd)} (${run.observations} daily returns)`} />
              <Def k="Data snapshot hash" v={<code className="text-xs">{run.dataSnapshotHash || '—'}</code>} />
              <Def k="Universe" v={run.symbols.join(', ') || '—'} />
              <Def k="Covariance" v={`${run.settings.covarianceMethod}${run.settings.covarianceMethod === 'LedoitWolf' ? ` (shrinkage ${pct(a.covarianceShrinkage, 1)})` : ''}`} />
              <Def k="Weights" v={`min ${pct(run.settings.minWeight, 0)} · max ${pct(run.settings.maxWeight, 0)} · cash ${pct(run.settings.cashReserve, 1)}`} />
              <Def k="Holdings / min position" v={`${run.settings.maxHoldings ?? 'no limit'} / ${run.settings.minPositionWeight !== null ? pct(run.settings.minPositionWeight) : 'none'}`} />
              <Def k="Turnover limit" v={run.settings.maxTurnover !== null ? pct(run.settings.maxTurnover) : 'none'} />
              <Def k="Sector limits" v={Object.entries(run.settings.sectorLimits).map(([k, v]) => `${k} ≤ ${pct(v, 0)}`).join(', ') || 'none'} />
              <Def k="Risk-free rate · benchmark" v={`${pct(run.settings.riskFreeRate)} · ${run.settings.benchmarkSymbol}`} />
              <Def k="Solver" v={`${run.solverCalls} QP solve(s), ${run.solverIterations} ADMM iterations, ${num(run.durationMs, 0)} ms`} />
            </dl>
          </div>
        ) },
        { title: 'Data notes', content: dataNotes.length ? <DataNotesList items={dataNotes} /> : null },
      ]} />
    </div>
  )
}

/** Every stock of the universe with its weight – searchable and paged, since a universe can hold hundreds of stocks. */
function AllocationTable({ rows, currency }: { rows: Allocation[]; currency: string }) {
  const invested = rows.filter((r) => r.weight > 1e-6).length
  return (
    <SearchableTable
      label="stocks in the allocation"
      caption="Recommended allocation"
      rows={rows}
      rowKey={(r) => r.symbol}
      initialSort={{ key: 'w', dir: 'desc' }}
      dense pageSize={25} maxRows={12}
      placeholder="Search symbol, name, sector or currency"
      matches={(r, q) => r.symbol.toLowerCase().includes(q) || (r.name ?? '').toLowerCase().includes(q) || (r.sector ?? '').toLowerCase().includes(q) || (r.currency ?? '').toLowerCase() === q}
      breakdown={`${rows.length.toLocaleString()} stocks in the universe · ${invested.toLocaleString()} with a weight · ${(rows.length - invested).toLocaleString()} at 0 %`}
      columns={[
        { key: 's', header: 'Symbol', sortValue: (r) => r.symbol, cell: (r) => <div><div className="font-medium">{r.symbol}</div><div className="text-xs text-muted-foreground">{r.name}</div></div> },
        { key: 'sec', header: 'Sector', sortValue: (r) => r.sector, cell: (r) => <span className="text-xs">{r.sector ?? '—'}</span> },
        {
          key: 'ccy', header: 'Trades in', sortValue: (r) => r.currency ?? '',
          cell: (r) => <span className={r.currency && r.currency !== currency ? 'text-xs font-medium' : 'text-xs text-muted-foreground'} title={r.currency && r.currency !== currency ? `Returns converted from ${r.currency} to ${currency}` : undefined}>{r.currency ?? '—'}</span>,
        },
        { key: 'w', header: 'Weight', align: 'right', sortValue: (r) => r.weight, cell: (r) => <span className={r.weight > 1e-6 ? 'font-medium' : 'text-muted-foreground'}>{pct(r.weight)}</span> },
        { key: 'mu', header: `Exp. return (${currency})`, align: 'right', sortValue: (r) => r.expectedReturn, cell: (r) => pct(r.expectedReturn) },
        { key: 'vol', header: 'Volatility', align: 'right', sortValue: (r) => r.volatility, cell: (r) => pct(r.volatility) },
        { key: 'rc', header: 'Risk contrib.', align: 'right', sortValue: (r) => r.riskContribution, cell: (r) => pct(r.riskContribution) },
      ]}
    />
  )
}

function Def({ k, v }: { k: string; v: ReactNode }) {
  return (
    <div>
      <dt className="text-xs text-muted-foreground">{k}</dt>
      <dd className="mt-0.5">{v}</dd>
    </div>
  )
}
