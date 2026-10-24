import { useMemo, useState, type ReactNode } from 'react'
import { Link, Navigate, useNavigate, useParams, useSearchParams } from 'react-router'
import { toast } from 'sonner'
import { AlertTriangle, ArrowRight, BrainCircuit, FileText, FlaskConical, ListChecks, Loader2, Play, RefreshCw, Trash2, X } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Progress } from '@/components/ui/progress'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { AllocationDonut, ChartCard, TimeSeriesChart, seriesColor, timeSeriesTable } from '@/components/charts'
import { CalibrationChart, SignedBarChart } from '@/components/analysis-charts'
import { DataTable, Pager, type Column } from '@/components/data-table'
import { DataBadge, EmptyState, ErrorState, InfoTip, LoadingBlock, MetricCard, PageHeader, ResultSection, SectionTitle, Warnings } from '@/components/common'
import { TradeProposalView } from '@/components/proposal'
import { PerformanceTable } from '@/components/performance-table'
import { StrategyInsights } from '@/components/strategy-insights'
import { interpretMlExperiment } from '@/lib/interpretation'
import { guideForModel } from '@/lib/strategy-guide'
import { errorMessage } from '@/lib/api'
import { portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { NoPortfolio } from '@/pages/shared'
import {
  useAssignExperimentPortfolio, useCreateMlExperiment, useDeleteMlExperiment, useMlBacktest, useMlCatalog, useMlExperiment, useMlExperiments, usePortfolio, usePredictAgain, useRebalancePreview,
} from '@/lib/queries'
import { useSelection } from '@/lib/selection'
import { useMissingDataGuard } from '@/components/missing-data-dialog'
import { yearsAgo } from '@/lib/data-check'
import type {
  AssetEligibility, MlCatalog, MlExperiment, MlExperimentRequest, MlExperimentSummary, MlPrediction, ModelFamily, PeriodMetrics, Portfolio, PredictionTarget,
  TradeProposal, WeightingMethod,
} from '@/lib/types'
import { date, dateTime, familyLabels, money, num, pct, relativeTime, targetLabels } from '@/lib/format'
import { cn } from '@/lib/utils'

const horizons = [5, 10, 21, 63, 126].map((h) => ({ value: h, label: `${h} trading days (≈ ${h <= 10 ? `${Math.round(h / 5)} week${h > 5 ? 's' : ''}` : `${Math.round(h / 21)} month${h > 21 ? 's' : ''}`})` }))

/**
 * ML Optimization of one portfolio (`/portfolios/:id/ml-optimize[/:experimentId]`), laid out like Math Optimization:
 * configuration and run history on the left, the selected experiment's results on the right – all sections visible.
 */
export function MlOptimizePage() {
  const { experimentId } = useParams()
  const { portfolioId, isLoading } = usePortfolioContext()
  const list = useMlExperiments(1, { portfolioId })
  const activeId = experimentId ?? list.data?.items[0]?.id
  const experiment = useMlExperiment(activeId)

  if (!isLoading && !portfolioId) return <NoPortfolio />
  // An experiment always opens inside its own portfolio (like a Math Optimization run).
  const owner = experiment.data?.summary.portfolioId
  if (portfolioId && owner && owner !== portfolioId) return <Navigate to={portfolioPath(owner, `ml-optimize/${experiment.data!.summary.id}`)} replace />

  return (
    <>
      <PageHeader
        level={2}
        title="ML Optimization"
        description="Configure a machine-learning model for this portfolio's universe, train and test it chronologically, then review its ranking-based allocation and confirm a simulated trade proposal. A result never changes holdings by itself."
      />
      <div className="grid gap-6 xl:grid-cols-[400px_minmax(0,1fr)]">
        <div className="space-y-4">
          {portfolioId && <NewExperimentCard portfolioId={portfolioId} />}
          {portfolioId && <ExperimentHistory portfolioId={portfolioId} activeId={activeId} />}
          {portfolioId && <UnlinkedExperiments portfolioId={portfolioId} />}
        </div>
        <div className="min-w-0">
          {activeId ? <ExperimentResults id={activeId} portfolioId={portfolioId!} /> : list.isLoading ? <LoadingBlock height="h-96" /> : (
            <EmptyState icon={<BrainCircuit className="size-8" />} title="No ML optimization yet"
              description="Configure the model on the left and click Run ML optimization." />
          )}
        </div>
      </div>
    </>
  )
}

// ------------------------------------------------------------------ configuration

const defaults: Omit<MlExperimentRequest, 'universe' | 'benchmarkSymbol' | 'riskFreeRate'> = {
  name: null,
  modelFamily: 'RidgeRegression',
  target: 'ForwardExcessReturn',
  horizonDays: 21,
  historyYears: 8,
  features: [],
  sampleStepDays: 5,
  trainFraction: 0.6,
  validationFraction: 0.2,
  embargoDays: 0,
  hyperparameters: null,
  seed: 42,
  topK: 10,
  weighting: 'EqualWeight',
  maxWeight: 0.2,
  cashReserve: 0,
  minimumScore: null,
  maxTurnover: null,
  sectorLimits: {},
  rebalance: 'Monthly',
  retrainEveryRebalances: 3,
  trainingWindowDays: null,
  transactionCostBps: 10,
  initialCapital: 100_000,
}

function NewExperimentCard({ portfolioId }: { portfolioId: string }) {
  const catalog = useMlCatalog()
  const portfolio = usePortfolio(portfolioId)
  if (catalog.error) return <ErrorState error={catalog.error} />
  if (portfolio.error) return <ErrorState error={portfolio.error} />
  if (!catalog.data || !portfolio.data) return <LoadingBlock height="h-[520px]" />
  // Keyed by portfolio: switching portfolio starts from that portfolio's universe and settings.
  return <NewExperimentForm key={portfolioId} catalog={catalog.data} portfolio={portfolio.data} />
}

function NewExperimentForm({ catalog, portfolio }: { catalog: MlCatalog; portfolio: Portfolio }) {
  const selection = useSelection()
  const create = useCreateMlExperiment()
  const navigate = useNavigate()
  const missingData = useMissingDataGuard()
  // Like Math Optimization: the portfolio's universe, benchmark and risk-free rate; computed in its currency.
  const [form, setForm] = useState({ ...defaults, benchmarkSymbol: portfolio.settings.benchmarkSymbol, riskFreeRate: portfolio.settings.riskFreeRate })
  // "from=selection" (selection tray) starts from the stock selection instead, like Math Optimization.
  const [searchParams] = useSearchParams()
  const fromSelection = searchParams.get('from') === 'selection' && selection.count > 0
  const [universe, setUniverse] = useState<string[]>(fromSelection ? selection.symbols : portfolio.settings.universe)
  const [allFeatures, setAllFeatures] = useState(true)
  const set = <K extends keyof typeof form>(key: K, value: (typeof form)[K]) => setForm((f) => ({ ...f, [key]: value }))
  const universeOptions = [...new Set([...portfolio.settings.universe, ...selection.symbols, ...universe])].sort()

  const family = catalog.modelFamilies.find((f) => f.value === form.modelFamily)!
  const targets = catalog.targets.filter((t) => family.supportedTargets.includes(t.value as PredictionTarget))
  const featureGroups = useMemo(() => {
    const groups = new Map<string, MlCatalog['features']>()
    catalog.features.forEach((f) => groups.set(f.group, [...(groups.get(f.group) ?? []), f]))
    return [...groups.entries()]
  }, [catalog.features])

  const changeFamily = (value: ModelFamily) => {
    const f = catalog.modelFamilies.find((m) => m.value === value)!
    setForm((s) => ({ ...s, modelFamily: value, target: f.supportedTargets.includes(s.target) ? s.target : f.supportedTargets[0] }))
  }

  // Missing prices or exchange rates for the requested history are offered for download first.
  const submit = () => {
    return missingData.guard({ symbols: universe, benchmarkSymbol: form.benchmarkSymbol, from: yearsAgo(form.historyYears), currency: portfolio.baseCurrency }, start)
  }
  const start = async () => {
    try {
      // Computed in the portfolio's currency: stocks of other markets are converted with dated exchange rates.
      const result = await create.mutateAsync({ ...form, universe, features: allFeatures ? [] : form.features, currency: portfolio.baseCurrency, portfolioId: portfolio.id })
      toast.success('ML optimization queued', { description: 'Training, validation, testing and the backtest run in the background.' })
      navigate(portfolioPath(portfolio.id, `ml-optimize/${result.summary.id}`))
    } catch (e) {
      toast.error('Could not start the experiment', { description: errorMessage(e) })
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Configuration</CardTitle>
        <CardDescription className="text-xs">
          {portfolio.name}, computed in {portfolio.baseCurrency}. Model settings decide what is predicted; portfolio construction decides how predictions become weights.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <fieldset className="space-y-2">
          <legend className="mb-1 flex w-full items-center justify-between text-sm font-medium">
            Equity universe <span className="text-xs font-normal text-muted-foreground">{universe.length} selected</span>
          </legend>
          <div className="flex flex-wrap gap-1.5">
            {selection.count > 0 && (
              <Button type="button" size="xs" variant="outline" onClick={() => setUniverse(selection.symbols)}><ListChecks /> Use stock selection ({selection.count})</Button>
            )}
            <Button type="button" size="xs" variant="ghost" render={<Link to="/stocks" />}>Browse stocks</Button>
            {universe.length > 0 && <Button type="button" size="xs" variant="ghost" onClick={() => setUniverse([])}><X /> Clear</Button>}
          </div>
          <div className="grid max-h-56 grid-cols-2 gap-1 overflow-y-auto rounded-md border p-2">
            {universeOptions.map((symbol) => (
              <label key={symbol} className="flex cursor-pointer items-center gap-2 rounded px-1.5 py-1 text-sm hover:bg-muted">
                <Checkbox checked={universe.includes(symbol)} aria-label={symbol}
                  onCheckedChange={(on) => setUniverse((u) => (on ? [...u, symbol] : u.filter((x) => x !== symbol)))} />
                <span className="font-medium">{symbol}</span>
              </label>
            ))}
          </div>
        </fieldset>

        <section className="space-y-3">
          <h3 className="text-xs font-semibold tracking-wide text-muted-foreground uppercase">Model</h3>
          <Field label="Model family" hint={family.description}>
            <Select value={form.modelFamily} onValueChange={(v) => changeFamily(v as ModelFamily)} items={catalog.modelFamilies.map((m) => ({ value: m.value, label: familyLabels[m.value] }))}>
              <SelectTrigger className="w-full" aria-label="Model family"><SelectValue /></SelectTrigger>
              <SelectContent>{catalog.modelFamilies.map((m) => <SelectItem key={m.value} value={m.value}>{familyLabels[m.value]}</SelectItem>)}</SelectContent>
            </Select>
          </Field>
          <Field label="Prediction target" hint={targets.find((t) => t.value === form.target)?.description}>
            <Select value={form.target} onValueChange={(v) => set('target', v as PredictionTarget)} items={targets.map((t) => ({ value: t.value, label: t.label }))}>
              <SelectTrigger className="w-full" aria-label="Prediction target"><SelectValue /></SelectTrigger>
              <SelectContent>{targets.map((t) => <SelectItem key={t.value} value={t.value}>{t.label}</SelectItem>)}</SelectContent>
            </Select>
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Horizon" hint="How far ahead the target is measured, in trading days.">
              <Select value={form.horizonDays} onValueChange={(v) => set('horizonDays', Number(v))} items={horizons}>
                <SelectTrigger className="w-full" aria-label="Prediction horizon"><SelectValue /></SelectTrigger>
                <SelectContent>{horizons.map((h) => <SelectItem key={h.value} value={h.value}>{h.label}</SelectItem>)}</SelectContent>
              </Select>
            </Field>
            <NumberField label="History (years)" value={form.historyYears} min={3} max={20} onChange={(v) => set('historyYears', v)}
              hint="Price history loaded for training, validation and testing. More history gives more stable estimates." />
          </div>
          <div className="space-y-2">
            <div className="flex items-center gap-2">
              <Switch id="all-features" checked={allFeatures} onCheckedChange={setAllFeatures} />
              <Label htmlFor="all-features" className="font-normal">Use all {catalog.features.length} features</Label>
              <InfoTip>{catalog.missingValuePolicy}</InfoTip>
            </div>
            {!allFeatures && (
              <div className="max-h-56 space-y-2 overflow-y-auto rounded-md border p-2">
                {featureGroups.map(([group, features]) => (
                  <div key={group}>
                    <div className="mb-1 text-[11px] font-medium text-muted-foreground">{group.replace(/([a-z])([A-Z])/g, '$1 $2')}</div>
                    {features.map((f) => (
                      <label key={f.name} className="flex items-start gap-2 rounded px-1 py-0.5 text-xs hover:bg-muted" title={f.definition}>
                        <Checkbox
                          checked={form.features.includes(f.name)}
                          onCheckedChange={(on) => set('features', on ? [...form.features, f.name] : form.features.filter((x) => x !== f.name))}
                          aria-label={f.name}
                        />
                        <span><code>{f.name}</code> <span className="text-muted-foreground">– {f.definition}</span></span>
                      </label>
                    ))}
                  </div>
                ))}
              </div>
            )}
          </div>
        </section>

        <section className="space-y-3">
          <h3 className="text-xs font-semibold tracking-wide text-muted-foreground uppercase">Portfolio construction</h3>
          <div className="grid grid-cols-2 gap-3">
            <NumberField label="Stocks held (top K)" value={form.topK} min={1} max={100} onChange={(v) => set('topK', v)} hint="Number of highest-ranked stocks bought at each rebalance." />
            <Field label="Weighting" hint={catalog.weightingMethods.find((w) => w.value === form.weighting)?.description}>
              <Select value={form.weighting} onValueChange={(v) => set('weighting', v as WeightingMethod)} items={catalog.weightingMethods.map((w) => ({ value: w.value, label: w.label }))}>
                <SelectTrigger className="w-full" aria-label="Weighting method"><SelectValue /></SelectTrigger>
                <SelectContent>{catalog.weightingMethods.map((w) => <SelectItem key={w.value} value={w.value}>{w.label}</SelectItem>)}</SelectContent>
              </Select>
            </Field>
            <NumberField label="Max weight per stock (%)" value={form.maxWeight * 100} min={1} max={100} onChange={(v) => set('maxWeight', v / 100)} />
            <NumberField label="Cash reserve (%)" value={form.cashReserve * 100} min={0} max={50} onChange={(v) => set('cashReserve', v / 100)} />
            <NumberField label="Max turnover per rebalance (%)" value={form.maxTurnover === null || form.maxTurnover === undefined ? '' : form.maxTurnover * 100} min={0} max={100} optional
              onChange={(v) => set('maxTurnover', Number.isFinite(v) ? v / 100 : null)} hint="One-way turnover cap; the move towards the target is scaled down. Empty = no limit." />
            <NumberField label="Minimum score" value={form.minimumScore ?? ''} optional step={0.01}
              onChange={(v) => set('minimumScore', Number.isFinite(v) ? v : null)}
              hint="Only stocks scoring at least this are bought: a probability for the outperformance target (e.g. 0.55), a predicted return otherwise. Empty = no threshold." />
            <Field label="Rebalancing">
              <Select value={form.rebalance} onValueChange={(v) => set('rebalance', v as 'Monthly' | 'Quarterly')} items={[{ value: 'Monthly', label: 'Monthly' }, { value: 'Quarterly', label: 'Quarterly' }]}>
                <SelectTrigger className="w-full" aria-label="Rebalancing frequency"><SelectValue /></SelectTrigger>
                <SelectContent><SelectItem value="Monthly">Monthly</SelectItem><SelectItem value="Quarterly">Quarterly</SelectItem></SelectContent>
              </Select>
            </Field>
            <NumberField label="Transaction cost (bps)" value={form.transactionCostBps} min={0} max={500} onChange={(v) => set('transactionCostBps', v)} />
          </div>
        </section>

        <details className="rounded-md border px-3 py-2">
          <summary className="cursor-pointer text-sm font-medium">Advanced: validation &amp; training</summary>
          <div className="mt-3 grid grid-cols-2 gap-3">
            <NumberField label="Training share (%)" value={form.trainFraction * 100} min={30} max={80} onChange={(v) => set('trainFraction', v / 100)}
              hint="Earliest share of decision dates used for training. Splits are chronological, never random." />
            <NumberField label="Validation share (%)" value={form.validationFraction * 100} min={10} max={40} onChange={(v) => set('validationFraction', v / 100)}
              hint="Next share used only to choose hyperparameters; the rest is the untouched test period." />
            <NumberField label="Embargo (days)" value={form.embargoDays} min={0} max={63} onChange={(v) => set('embargoDays', v)}
              hint="Extra gap after purging between periods (labels already never overlap the next period)." />
            <NumberField label="Sample step (days)" value={form.sampleStepDays} min={1} max={21} onChange={(v) => set('sampleStepDays', v)}
              hint="Spacing of decision dates; overlapping horizons make nearby samples dependent." />
            <NumberField label="Retrain every N rebalances" value={form.retrainEveryRebalances} min={0} max={24} onChange={(v) => set('retrainEveryRebalances', v)}
              hint="Walk-forward retraining in the backtest using only labels already realized. 0 = keep the evaluation model." />
            <NumberField label="Seed" value={form.seed} min={0} max={2147483647} onChange={(v) => set('seed', v)} hint="Fixes randomness (tree bagging) for reproducible results." />
          </div>
          <p className="mt-2 text-[11px] text-muted-foreground">{catalog.validationMethodology}</p>
        </details>

        <Button className="w-full" onClick={submit} disabled={create.isPending || missingData.checking || universe.length < 5}>
          {create.isPending || missingData.checking ? <Loader2 className="animate-spin" /> : <Play />} {create.isPending ? 'Starting…' : 'Run ML optimization'}
        </Button>
        {missingData.dialog}
        {universe.length > 0 && universe.length < 5 && <p className="text-xs text-warn">Select at least 5 stocks.</p>}
      </CardContent>
    </Card>
  )
}

function Field({ label, hint, children }: { label: string; hint?: ReactNode; children: ReactNode }) {
  return (
    <div className="space-y-1.5">
      <Label className="flex items-center gap-1 text-xs">{label}{hint && <InfoTip>{hint}</InfoTip>}</Label>
      {children}
    </div>
  )
}

function NumberField({ label, value, onChange, min, max, step, hint, optional }: {
  label: string; value: number | ''; onChange: (v: number) => void; min?: number; max?: number; step?: number; hint?: ReactNode; optional?: boolean
}) {
  const id = label.replace(/\W+/g, '-').toLowerCase()
  return (
    <div className="space-y-1.5">
      <Label htmlFor={id} className="flex items-center gap-1 text-xs">{label}{hint && <InfoTip>{hint}</InfoTip>}</Label>
      <Input id={id} type="number" value={value} min={min} max={max} step={step ?? 1} placeholder={optional ? 'None' : undefined}
        onChange={(e) => onChange(e.target.value === '' ? NaN : Number(e.target.value))} />
    </div>
  )
}

// ------------------------------------------------------------------ history

function StatusBadge({ status }: { status: MlExperimentSummary['status'] }) {
  const cls = status === 'Succeeded' ? 'border-gain/40 text-gain' : status === 'Failed' || status === 'Cancelled' ? 'border-loss/40 text-loss' : 'border-warn/50 text-warn'
  return <Badge variant="outline" className={cls}>{status === 'Running' && <Loader2 className="size-3 animate-spin" />}{status}</Badge>
}

function ExperimentHistory({ portfolioId, activeId }: { portfolioId: string; activeId?: string }) {
  const [page, setPage] = useState(1)
  const list = useMlExperiments(page, { portfolioId })
  const navigate = useNavigate()
  if (list.data && list.data.items.length === 0) return null
  return (
    <Card>
      <CardHeader><CardTitle className="text-sm">Run history</CardTitle></CardHeader>
      <CardContent>
        {list.error ? <ErrorState error={list.error} /> : !list.data ? <LoadingBlock height="h-40" /> : list.data.items.length === 0 ? (
          <p className="text-sm text-muted-foreground">No ML optimization yet.</p>
        ) : (
          <>
            <ul className="divide-y rounded-md border">
              {list.data.items.map((e) => (
                <li key={e.id}>
                  <button type="button" onClick={() => navigate(portfolioPath(portfolioId, `ml-optimize/${e.id}`))}
                    className={cn('w-full px-3 py-2 text-left text-sm hover:bg-muted', e.id === activeId && 'bg-muted')} aria-current={e.id === activeId ? 'true' : undefined}>
                    <div className="flex items-center justify-between gap-2">
                      <span className="truncate font-medium">{e.name}</span>
                      <StatusBadge status={e.status} />
                    </div>
                    <div className="mt-0.5 flex flex-wrap gap-x-3 text-xs text-muted-foreground tabular">
                      <span>{relativeTime(e.createdAtUtc)}</span>
                      {e.testRankCorrelation !== null && <span>test IC {num(e.testRankCorrelation, 3)}</span>}
                      {e.testRocAuc !== null && <span>AUC {num(e.testRocAuc, 3)}</span>}
                      {e.usesSimulatedData && <span className="text-foreground">simulated</span>}
                    </div>
                  </button>
                </li>
              ))}
            </ul>
            <Pager page={page} totalPages={list.data.totalPages} onChange={setPage} />
          </>
        )}
      </CardContent>
    </Card>
  )
}

/**
 * Experiments created before portfolios were recorded have none. They are listed in every portfolio of their currency
 * and become this portfolio's only when the user links them – never by guessing.
 */
function UnlinkedExperiments({ portfolioId }: { portfolioId: string }) {
  const { portfolios } = usePortfolioContext()
  const portfolio = portfolios.find((p) => p.id === portfolioId)
  const list = useMlExperiments(1, { unlinked: true })
  const items = (list.data?.items ?? []).filter((e) => e.currency === portfolio?.baseCurrency)
  if (!portfolio || items.length === 0) return null
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Earlier ML experiments without a portfolio</CardTitle>
        <CardDescription className="text-xs">
          Created before experiments belonged to a portfolio. Link one to “{portfolio.name}” to list it here and to backtest and compare it with this
          portfolio&apos;s optimizations. Only {portfolio.baseCurrency} experiments are shown.
        </CardDescription>
      </CardHeader>
      <CardContent className="max-h-72 space-y-1 overflow-y-auto">
        {items.map((e) => <UnlinkedExperimentRow key={e.id} experiment={e} portfolioId={portfolioId} />)}
      </CardContent>
    </Card>
  )
}

function UnlinkedExperimentRow({ experiment, portfolioId }: { experiment: MlExperimentSummary; portfolioId: string }) {
  const assign = useAssignExperimentPortfolio(experiment.id)
  return (
    <div className="flex items-center justify-between gap-2 rounded-md px-2.5 py-1.5 text-sm hover:bg-muted">
      <Link to={portfolioPath(portfolioId, `ml-optimize/${experiment.id}`)} className="min-w-0">
        <div className="truncate font-medium">{experiment.name}</div>
        <div className="text-xs text-muted-foreground">{relativeTime(experiment.createdAtUtc)} · {experiment.status}</div>
      </Link>
      <Button size="xs" variant="outline" disabled={assign.isPending} onClick={async () => {
        try {
          await assign.mutateAsync(portfolioId)
          toast.success('Experiment linked to the portfolio')
        } catch (err) {
          toast.error('Could not link the experiment', { description: errorMessage(err) })
        }
      }}>Link</Button>
    </div>
  )
}

// ------------------------------------------------------------------ experiment detail

function ExperimentResults({ id, portfolioId }: { id: string; portfolioId: string }) {
  const experiment = useMlExperiment(id)
  const remove = useDeleteMlExperiment()
  const predictAgain = usePredictAgain(id)
  const navigate = useNavigate()
  if (experiment.error) return <ErrorState error={experiment.error} onRetry={() => experiment.refetch()} />
  if (!experiment.data) return <LoadingBlock height="h-96" />
  const e = experiment.data
  const s = e.summary
  const active = s.status === 'Queued' || s.status === 'Running'
  const ok = s.status === 'Succeeded' && !!e.results

  return (
    <div className="space-y-4">
      {/* Header like Math Optimization: name, status, data badges, currency, date and actions. */}
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-lg font-semibold">{s.name}</h2>
        <StatusBadge status={s.status} />
        {s.usesSimulatedData ? <DataBadge kind="simulated" /> : <DataBadge kind="estimate" label="Model estimate" />}
        {s.isExploratory && <Badge variant="outline" className="border-warn/50 text-warn" title="Too few independent test periods for robust conclusions">Exploratory</Badge>}
        <Badge variant="outline">In {e.currency}</Badge>
        <span className="text-xs text-muted-foreground">{dateTime(s.createdAtUtc)}</span>
        <div className="ml-auto flex flex-wrap gap-1.5">
          {ok && (
            <Button size="sm" variant="outline" disabled={predictAgain.isPending} onClick={async () => {
              try {
                await predictAgain.mutateAsync()
                toast.success('New predictions stored', { description: 'Scored with the stored deployment model (not retrained).' })
              } catch (err) {
                toast.error('Prediction failed', { description: errorMessage(err) })
              }
            }}>{predictAgain.isPending ? <Loader2 className="animate-spin" /> : <RefreshCw />} Predict on latest data</Button>
          )}
          {!active && (
            <Button size="sm" variant="ghost" aria-label="Delete experiment" onClick={async () => {
              try {
                await remove.mutateAsync(id)
                toast.success('ML optimization deleted')
                navigate(portfolioPath(portfolioId, 'ml-optimize'))
              } catch (err) {
                toast.error('Delete failed', { description: errorMessage(err) })
              }
            }}><Trash2 /></Button>
          )}
        </div>
      </div>
      <p className="text-xs text-muted-foreground">
        {familyLabels[s.modelFamily]} · {targetLabels[s.target]} · {s.horizonDays}-day horizon · {e.symbols.length} stocks · benchmark {e.benchmarkSymbol}
      </p>
      <ExperimentPortfolio id={id} experimentPortfolioId={s.portfolioId ?? null} currency={e.currency} portfolioId={portfolioId} />

      {active && (
        <div className="space-y-1" aria-live="polite">
          <Progress value={s.progressPercent} aria-label="Experiment progress" />
          <p className="text-xs text-muted-foreground">{s.progressMessage ?? 'Queued'} · {s.progressPercent}%</p>
        </div>
      )}
      {s.status === 'Failed' && (
        <Alert variant="destructive"><AlertTriangle /><AlertTitle>The ML optimization did not complete</AlertTitle><AlertDescription>{s.error}</AlertDescription></Alert>
      )}

      {ok && <HeadlineMetrics e={e} />}
      {/* Every result is visible at once (no tabs); the same section order as Math Optimization. */}
      {ok && <ResultSection title="Allocation"><AllocationSection e={e} /></ResultSection>}
      {ok && <ResultSection title="Validation & test" hint={e.results!.validationMethodology}><EvaluationSection e={e} /></ResultSection>}
      {ok && <ResultSection title="Walk-forward backtest (test period)"><BacktestSection id={id} /></ResultSection>}
      {ok && <ResultSection title="Explainability"><ExplainSection e={e} /></ResultSection>}
      {ok && <ResultSection title="Inputs & methodology" collapsible><SetupSection e={e} /></ResultSection>}
      {ok && <MlTradeProposal e={e} portfolioId={portfolioId} />}
      <Warnings items={e.warnings} title="Data and methodology notes" collapsible />
      <p className="text-[11px] text-muted-foreground">{e.disclaimer}</p>
      {ok && <MlInsights e={e} />}
    </div>
  )
}

/** The headline numbers, in the same place as Math Optimization's expected return, volatility, Sharpe and holdings. */
function HeadlineMetrics({ e }: { e: MlExperiment }) {
  const test = e.results!.testMetrics
  const set = e.latestPredictions
  const held = set?.predictions.filter((p) => p.targetWeight > 0).length ?? 0
  return (
    <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
      <MetricCard label="Rank correlation (IC)" value={f3(test.ranking.meanInformationCoefficient)} numeric={test.ranking.meanInformationCoefficient} tone="signed"
        hint="Mean Spearman correlation per date between scores and realized forward excess returns on the untouched test period. 0 = no ranking skill."
        sub={test.ranking.informationCoefficientTStat !== null ? `t = ${num(test.ranking.informationCoefficientTStat, 2)} · test period` : 'Test period'} />
      <MetricCard label={`Top-${test.ranking.topK} excess vs universe`} value={pct(test.ranking.topKExcessOverUniverse)} numeric={test.ranking.topKExcessOverUniverse} tone="signed"
        sub="Test period, before costs" hint="Average realized forward excess return of the top-K stocks minus the universe average, per decision date." />
      {test.classification ? (
        <MetricCard label="ROC-AUC" value={f3(test.classification.rocAuc)} sub="Test period" hint="Probability that a random outperformer is scored above a random underperformer. 0.5 = no skill." />
      ) : (
        <MetricCard label="Out-of-sample R²" value={f3(test.regression!.outOfSampleR2)} numeric={test.regression!.outOfSampleR2} tone="signed" sub="Test period"
          hint="1 − SSE/SSE of always predicting the training mean. Negative = worse than that naive forecast; small values are normal for returns." />
      )}
      <MetricCard label="Holdings · cash" value={set ? `${held} · ${pct(set.cashWeight, 1)}` : '—'} sub={set ? `Predictions as of ${date(set.asOfDate)}` : undefined} />
    </div>
  )
}

/** Trade proposal for this portfolio (from the URL), in the same card as Math Optimization's. */
function MlTradeProposal({ e, portfolioId }: { e: MlExperiment; portfolioId: string }) {
  const { portfolios } = usePortfolioContext()
  const portfolio = portfolios.find((p) => p.id === portfolioId)
  const preview = useRebalancePreview(portfolioId)
  const [created, setCreated] = useState<{ id: string; proposal: TradeProposal } | null>(null)
  const proposal = created?.id === e.summary.id ? created.proposal : null
  const weights = Object.fromEntries((e.latestPredictions?.predictions ?? []).filter((p) => p.targetWeight > 0).map((p) => [p.symbol, p.targetWeight]))
  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between gap-2">
        <div>
          <CardTitle className="flex items-center gap-2 text-sm"><FileText className="size-4" /> Trade proposal</CardTitle>
          <CardDescription className="text-xs">Translate the ML target weights into whole-share orders for “{portfolio?.name ?? 'this portfolio'}” at the latest stored prices, after costs. Nothing is executed until you confirm.</CardDescription>
        </div>
        <Button variant={proposal ? 'outline' : 'default'} disabled={preview.isPending || Object.keys(weights).length === 0} onClick={async () => {
          try {
            setCreated({ id: e.summary.id, proposal: await preview.mutateAsync({ targetWeights: weights }) })
          } catch (err) {
            toast.error('Could not create trade proposal', { description: errorMessage(err) })
          }
        }}>
          {preview.isPending ? <Loader2 className="animate-spin" /> : <ArrowRight />}
          {proposal ? 'Recalculate' : 'Create trade proposal'}
        </Button>
      </CardHeader>
      {proposal && <CardContent><TradeProposalView proposal={proposal} currency={portfolio?.baseCurrency ?? e.currency} /></CardContent>}
    </Card>
  )
}

/**
 * The portfolio the experiment belongs to. Experiments created before portfolios were recorded can be assigned here –
 * explicitly, to a portfolio of the same currency – so they can be backtested and compared within it.
 */
function ExperimentPortfolio({ id, experimentPortfolioId, currency, portfolioId }: { id: string; experimentPortfolioId: string | null; currency: string; portfolioId: string }) {
  const { portfolios } = usePortfolioContext()
  const assign = useAssignExperimentPortfolio(id)
  if (experimentPortfolioId) return null
  const portfolio = portfolios.find((p) => p.id === portfolioId)
  const sameCurrency = portfolio?.baseCurrency === currency
  return (
    <div className="flex flex-wrap items-center gap-2 rounded-md border border-dashed p-2 text-xs">
      <span className="text-muted-foreground">
        This experiment belongs to no portfolio yet (created before experiments were linked).{' '}
        {sameCurrency ? `Link it to “${portfolio?.name}” to backtest and compare it with this portfolio's optimizations.` : `It was computed in ${currency}; this portfolio uses ${portfolio?.baseCurrency}, so it cannot be linked here.`}
      </span>
      {sameCurrency && (
        <Button size="xs" disabled={assign.isPending} onClick={async () => {
          try {
            await assign.mutateAsync(portfolioId)
            toast.success('Experiment linked to the portfolio')
          } catch (err) {
            toast.error('Could not link the experiment', { description: errorMessage(err) })
          }
        }}>Link to this portfolio</Button>
      )}
    </div>
  )
}

/** Interpretation of this experiment's own numbers (test metrics, portfolio, backtest) with a link to the model guide. */
function MlInsights({ e }: { e: MlExperiment }) {
  const bt = useMlBacktest(e.summary.id, !!e.results)
  return <StrategyInsights insights={interpretMlExperiment(e, bt.data)} guideId={guideForModel(e.configuration.modelFamily)} collapsible />
}

const f3 = (v: number | null | undefined) => (v === null || v === undefined ? '—' : num(v, 3))

function PeriodTimeline({ e }: { e: MlExperiment }) {
  const r = e.results!
  const periods = [
    { name: 'Training', p: r.trainPeriod, cls: 'bg-series-1/70' },
    { name: 'Validation', p: r.validationPeriod, cls: 'bg-series-4/70' },
    { name: 'Test (out-of-sample)', p: r.testPeriod, cls: 'bg-series-3/80' },
  ]
  const t0 = new Date(r.trainPeriod.start).getTime()
  const t1 = new Date(r.testPeriod.end).getTime()
  return (
    <div className="space-y-2">
      <div className="flex h-3 overflow-hidden rounded-full bg-muted" role="img" aria-label="Chronological periods">
        {periods.map((x) => {
          const left = ((new Date(x.p.start).getTime() - t0) / (t1 - t0)) * 100
          const width = ((new Date(x.p.end).getTime() - new Date(x.p.start).getTime()) / (t1 - t0)) * 100
          return <div key={x.name} className={cn('h-full', x.cls)} style={{ marginLeft: periods[0] === x ? `${left}%` : 2, width: `${Math.max(1, width)}%` }} title={x.name} />
        })}
      </div>
      <div className="grid gap-2 text-xs sm:grid-cols-3">
        {periods.map((x) => (
          <div key={x.name}>
            <div className="font-medium">{x.name}</div>
            <div className="text-muted-foreground tabular">{date(x.p.start)} – {date(x.p.end)}</div>
            <div className="text-muted-foreground tabular">{num(x.p.samples, 0)} samples · {x.p.decisionDates} dates{x.p.purged > 0 ? ` · ${x.p.purged} purged` : ''}</div>
          </div>
        ))}
      </div>
    </div>
  )
}

function EvaluationSection({ e }: { e: MlExperiment }) {
  const r = e.results!
  const test = r.testMetrics
  const isClass = !!test.classification
  return (
    <>
      <Card><CardContent className="space-y-3 py-4">
        <SectionTitle hint={r.validationMethodology}>Chronological validation</SectionTitle>
        <PeriodTimeline e={e} />
        <p className="text-xs text-muted-foreground"><span className="font-medium text-foreground">Target: </span>{r.targetDefinition}</p>
      </CardContent></Card>

      <Card><CardContent className="py-4">
        <SectionTitle hint="In-sample training metrics are optimistic and shown only for comparison; decisions should rest on the test column.">Train vs validation vs test</SectionTitle>
        <MetricsTable periods={[r.trainMetrics, r.validationMetrics, r.testMetrics]} />
      </CardContent></Card>

      <Card><CardContent className="py-4">
        <SectionTitle hint="Naive references on the same test period. A model is only useful if it beats them.">Baselines (test period)</SectionTitle>
        <div className="overflow-x-auto">
          <table className="w-full text-sm tabular">
            <thead><tr className="text-left text-xs text-muted-foreground"><th className="py-1">Baseline</th><th>IC</th><th>Top-K excess</th><th>{isClass ? 'Brier' : 'RMSE'}</th><th>Description</th></tr></thead>
            <tbody>
              <tr className="border-t font-medium"><td className="py-1.5">This model</td><td>{f3(test.ranking.meanInformationCoefficient)}</td><td>{pct(test.ranking.topKExcessOverUniverse)}</td>
                <td>{isClass ? f3(test.classification!.brier) : pct(test.regression!.rmse)}</td><td className="text-xs text-muted-foreground">Selected configuration, refit on train + validation</td></tr>
              {r.baselines.map((b) => (
                <tr key={b.name} className="border-t">
                  <td className="py-1.5">{b.name}</td>
                  <td>{f3(b.ranking?.meanInformationCoefficient)}</td>
                  <td>{b.ranking ? pct(b.ranking.topKExcessOverUniverse) : '—'}</td>
                  <td>{b.classification ? f3(b.classification.brier) : b.regression ? pct(b.regression.rmse) : '—'}</td>
                  <td className="text-xs text-muted-foreground">{b.description}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </CardContent></Card>

      {isClass && test.classification!.calibration.length > 0 && (
        <ChartCard title="Calibration (test period)" hint="Points on the dashed diagonal mean predicted probabilities match observed frequencies."
          table={{ columns: ['Mean predicted', 'Observed', 'Count'], rows: test.classification!.calibration.map((b) => [pct(b.meanPredicted, 1), pct(b.observedFrequency, 1), b.count]) }}>
          <CalibrationChart bins={test.classification!.calibration} />
          <p className="mt-2 text-xs text-muted-foreground">Base rate {pct(test.classification!.baseRate, 1)} · Brier {f3(test.classification!.brier)} vs {f3(test.classification!.baseRateBrier)} for always predicting the training base rate.</p>
        </ChartCard>
      )}

      <Card><CardContent className="py-4">
        <SectionTitle hint="Each hyperparameter set was trained on the training period and scored on the validation period only. The lowest validation loss was selected.">Hyperparameter search (validation)</SectionTitle>
        <table className="w-full text-xs tabular">
          <thead><tr className="text-left text-muted-foreground"><th className="py-1">Hyperparameters</th><th>Validation loss</th><th>Validation rank corr.</th></tr></thead>
          <tbody>{r.trials.map((t, i) => {
            const chosen = JSON.stringify(t.hyperparameters) === JSON.stringify(r.selectedHyperparameters)
            return (
              <tr key={i} className={cn('border-t', chosen && 'font-semibold')}>
                <td className="py-1">{Object.entries(t.hyperparameters).map(([k, v]) => `${k}=${v}`).join(', ')}{chosen && ' ✓ selected'}</td>
                <td>{t.validationLoss.toPrecision(4)}</td><td>{f3(t.validationRankCorrelation)}</td>
              </tr>
            )
          })}</tbody>
        </table>
      </CardContent></Card>
    </>
  )
}

function MetricsTable({ periods }: { periods: PeriodMetrics[] }) {
  const rows: [string, (p: PeriodMetrics) => string][] = [
    ['Rank correlation (IC)', (p) => f3(p.ranking.meanInformationCoefficient)],
    ['Top-K excess vs universe', (p) => pct(p.ranking.topKExcessOverUniverse)],
    ['Top-K hit rate', (p) => pct(p.ranking.topKHitRate, 1)],
    ['Top − bottom spread', (p) => pct(p.ranking.topMinusBottomSpread)],
    ['Ranking stability', (p) => f3(p.ranking.rankStability)],
  ]
  if (periods[0].regression) rows.push(
    ['MAE', (p) => pct(p.regression!.mae)],
    ['RMSE', (p) => pct(p.regression!.rmse)],
    ['Out-of-sample R²', (p) => f3(p.regression!.outOfSampleR2)],
    ['Directional accuracy', (p) => pct(p.regression!.directionalAccuracy, 1)],
  )
  if (periods[0].classification) rows.push(
    ['ROC-AUC', (p) => f3(p.classification!.rocAuc)],
    ['Brier score', (p) => f3(p.classification!.brier)],
    ['Brier skill vs base rate', (p) => f3(p.classification!.brierSkillScore)],
    ['Log loss', (p) => f3(p.classification!.logLoss)],
    ['Precision @ 0.5', (p) => pct(p.classification!.precision, 1)],
    ['Recall @ 0.5', (p) => pct(p.classification!.recall, 1)],
  )
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm tabular">
        <thead><tr className="text-left text-xs text-muted-foreground"><th className="py-1">Metric</th>{periods.map((p) => <th key={p.period}>{p.period}</th>)}</tr></thead>
        <tbody>{rows.map(([name, fn]) => (
          <tr key={name} className="border-t"><td className="py-1.5 text-muted-foreground">{name}</td>{periods.map((p) => <td key={p.period}>{fn(p)}</td>)}</tr>
        ))}</tbody>
      </table>
    </div>
  )
}

function predictionFormat(target: PredictionTarget) {
  return (v: number) => (target === 'OutperformBenchmark' ? pct(v, 1) : pct(v, 2))
}

function AllocationSection({ e }: { e: MlExperiment }) {
  const set = e.latestPredictions
  if (!set) return <EmptyState title="No predictions" />
  const fmt = predictionFormat(e.summary.target)
  const valueLabel = e.summary.target === 'OutperformBenchmark' ? 'P(outperform)' : e.summary.target === 'ForwardVolatility' ? 'Predicted volatility' : `Predicted ${targetLabels[e.summary.target].toLowerCase()}`

  const columns: Column<MlPrediction>[] = [
    { key: 'rank', header: 'Rank', sortValue: (p) => p.rank, cell: (p) => p.rank },
    { key: 'symbol', header: 'Stock', sortValue: (p) => p.symbol, cell: (p) => <div><div className="font-semibold">{p.symbol}</div><div className="max-w-40 truncate text-[11px] text-muted-foreground">{p.name ?? ''}</div></div> },
    { key: 'sector', header: 'Sector', cell: (p) => <span className="text-xs">{p.sector ?? '—'}</span>, className: 'hidden lg:table-cell' },
    { key: 'price', header: 'Latest price', align: 'right', sortValue: (p) => p.latestPrice, cell: (p) => <span title={p.latestPriceDate ? `as of ${date(p.latestPriceDate)}` : ''}>{money(p.latestPrice)}</span> },
    {
      key: 'pred', header: valueLabel, align: 'right', sortValue: (p) => p.predictedValue,
      cell: (p) => (
        <div>
          <div className="font-medium">{fmt(p.predictedValue)}</div>
          {p.intervalLow !== null && <div className="text-[10px] text-muted-foreground">{fmt(p.intervalLow)} … {fmt(p.intervalHigh!)}</div>}
        </div>
      ),
    },
    { key: 'sel', header: 'Selected', sortValue: (p) => (p.selected ? 1 : 0), cell: (p) => p.selected ? <Badge variant="outline" className="border-gain/40 text-gain">Selected</Badge> : <span className="text-xs text-muted-foreground">—</span> },
    { key: 'w', header: 'Target weight', align: 'right', sortValue: (p) => p.targetWeight, cell: (p) => (p.targetWeight > 0 ? pct(p.targetWeight, 1) : '—') },
    {
      key: 'drivers', header: 'Main drivers',
      cell: (p) => (
        <ul className="space-y-0.5 text-[11px]">
          {p.drivers.slice(0, 3).map((d) => (
            <li key={d.feature} className="flex gap-1"><span className={d.contribution >= 0 ? 'text-gain' : 'text-loss'}>{d.contribution >= 0 ? '▲' : '▼'}</span><code>{d.feature}</code></li>
          ))}
        </ul>
      ),
      className: 'hidden md:table-cell',
    },
  ]

  const weights = Object.fromEntries(set.predictions.filter((p) => p.targetWeight > 0).map((p) => [p.symbol, p.targetWeight]))

  return (
    <>
      <p className="text-xs text-muted-foreground">
        Predictions as of <span className="font-medium text-foreground">{date(set.asOfDate)}</span> for the next {set.horizonDays} trading days ·
        model v{set.modelVersion} ({set.modelRole}, trained through {date(set.modelTrainedThrough)}) · created {dateTime(set.createdAtUtc)}
        {set.usesSimulatedData && <> · <DataBadge kind="simulated" /></>}
      </p>
      <Alert><FlaskConical /><AlertTitle>Estimates, not guarantees</AlertTitle><AlertDescription>
        Predicted values are uncertain model estimates. {set.intervalMethod ?? 'No uncertainty interval is shown for this target.'} Drivers: {e.results!.contributionMethod}
      </AlertDescription></Alert>
      <DataTable rows={set.predictions} columns={columns} rowKey={(p) => p.symbol} dense caption="Stock predictions and ranking" initialSort={{ key: 'rank', dir: 'asc' }} />

      <div className="grid gap-4 lg:grid-cols-2">
        <ChartCard title="ML portfolio" hint="Built from the ranking with the experiment's construction rules – not by the mean-variance optimizer."
          table={{ columns: ['Stock', 'Weight'], rows: [...Object.entries(weights).map(([k, v]) => [k, pct(v, 2)]), ['Cash', pct(set.cashWeight, 2)]] }}>
          <AllocationDonut items={[...Object.entries(weights).map(([name, value]) => ({ name, value })), ...(set.cashWeight > 1e-6 ? [{ name: 'Cash', value: set.cashWeight }] : [])]} />
        </ChartCard>
        <Card><CardContent className="space-y-3 py-4">
          <SectionTitle>How the weights were built</SectionTitle>
          <ul className="list-disc space-y-1 pl-4 text-sm">
            <li>Rank stocks by {e.summary.target === 'ForwardVolatility' ? 'lowest predicted volatility' : 'predicted value'}{e.configuration.minimumScore !== null && e.configuration.minimumScore !== undefined ? `, keep scores ≥ ${e.configuration.minimumScore}` : ''}.</li>
            <li>Select the top {e.configuration.topK}; weighting: {e.configuration.weighting.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()}.</li>
            <li>Cap each stock at {pct(e.configuration.maxWeight, 0)}; cash reserve {pct(e.configuration.cashReserve, 0)}.</li>
          </ul>
          <Warnings items={set.notes} title="Construction notes" />
        </CardContent></Card>
      </div>
    </>
  )
}

function ExplainSection({ e }: { e: MlExperiment }) {
  const r = e.results!
  const native = r.nativeExplanation
  const signed = native.method.toLowerCase().includes('coefficient')
  // Loss increases are tiny (often 1e-4…1e-2): show them on one shared power-of-ten scale instead of per-label exponents.
  const maxAbs = Math.max(...r.permutationImportance.map((p) => Math.abs(p.meanLossIncrease) + p.stdDev), Number.MIN_VALUE)
  const exponent = Math.floor(Math.log10(maxAbs))
  const scale = 10 ** exponent
  const scaled = (v: number) => (v / scale).toFixed(1)
  return (
    <div className="grid gap-4 lg:grid-cols-2">
      <ChartCard title="Permutation importance (test period)" hint="Increase in loss when one feature is shuffled. Measures how much the model relies on a feature, not causation; correlated features can mask each other."
        table={{ columns: ['Feature', 'Loss increase', '± sd'], rows: r.permutationImportance.map((p) => [p.feature, p.meanLossIncrease.toExponential(2), p.stdDev.toExponential(2)]) }}>
        <SignedBarChart items={r.permutationImportance.map((p) => ({ name: p.feature, value: p.meanLossIncrease, error: p.stdDev }))} format={scaled} />
        <p className="mt-2 text-xs text-muted-foreground">
          Loss increase in units of 10<sup>{exponent}</sup>; whiskers show ± one standard deviation across shuffles. Bars crossing zero are indistinguishable from noise.
        </p>
      </ChartCard>
      <ChartCard title={native.method} hint={native.interpretation}
        table={{ columns: ['Feature', 'Value'], rows: native.values.map((v) => [v.name, v.value.toPrecision(4)]) }}>
        <SignedBarChart items={native.values.map((v) => ({ name: v.name, value: v.value }))} signed={signed} format={(v) => (signed ? v.toPrecision(2) : pct(v, 0))} />
        <p className="mt-2 text-xs text-muted-foreground">{native.interpretation}</p>
      </ChartCard>
      <Card className="lg:col-span-2"><CardContent className="py-4 text-sm">
        <SectionTitle>Per-prediction drivers</SectionTitle>
        <p className="text-muted-foreground">{r.contributionMethod} Drivers for each stock are listed in the predictions table. They describe what the model associates with its output, not why prices move.</p>
      </CardContent></Card>
    </div>
  )
}

function BacktestSection({ id }: { id: string }) {
  const bt = useMlBacktest(id)
  if (bt.error) return <ErrorState error={bt.error} />
  if (!bt.data) return <LoadingBlock />
  const names = Object.keys(bt.data.values)
  const colors = names.map((_, i) => seriesColor(i === 0 ? 1 : i === 1 ? 3 : 4))
  const series = names.map((n, i) => ({ name: n, values: bt.data.values[n].map((v) => v / bt.data.values[n][0] - 1), color: colors[i] }))
  const dd = names.map((n, i) => ({ name: n, values: bt.data.drawdowns[n], color: colors[i] }))
  return (
    <>
      <Alert><AlertTriangle /><AlertTitle>Hypothetical walk-forward backtest</AlertTitle><AlertDescription>{bt.data.methodology}</AlertDescription></Alert>
      <ChartCard title="Cumulative return (test period)" legend={series.map((s) => ({ name: s.name, color: s.color }))} table={timeSeriesTable(bt.data.dates, series, (v) => pct(v))}>
        <TimeSeriesChart dates={bt.data.dates} series={series} format={(v) => pct(v, 0)} zeroLine />
      </ChartCard>
      <ChartCard title="Drawdown" legend={dd.map((s) => ({ name: s.name, color: s.color }))} table={timeSeriesTable(bt.data.dates, dd, (v) => pct(v))}>
        <TimeSeriesChart dates={bt.data.dates} series={dd} format={(v) => pct(v, 0)} height={200} />
      </ChartCard>
      <Card><CardContent className="py-4"><PerformanceTable items={bt.data.performance} colors={colors} /></CardContent></Card>
      <Card><CardContent className="py-4">
        <SectionTitle hint="Each rebalance uses a model trained only on labels realized before the signal date.">Rebalance log</SectionTitle>
        <div className="max-h-80 overflow-auto">
          <table className="w-full text-xs tabular">
            <thead className="sticky top-0 bg-card"><tr className="text-left text-muted-foreground"><th className="py-1">Signal</th><th>Model trained through</th><th>Samples</th><th>Retrained</th><th>Selected</th></tr></thead>
            <tbody>{bt.data.rebalances.map((r) => (
              <tr key={r.signalDate} className="border-t"><td className="py-1">{date(r.signalDate)}</td><td>{date(r.modelTrainedThrough)}</td><td>{num(r.modelTrainingSamples, 0)}</td>
                <td>{r.retrained ? 'yes' : ''}</td><td>{r.selected.join(', ')}</td></tr>
            ))}</tbody>
          </table>
        </div>
      </CardContent></Card>
      <Warnings items={bt.data.warnings} title="Backtest notes" />
    </>
  )
}

function SetupSection({ e }: { e: MlExperiment }) {
  const r = e.results!
  return (
    <>
      <Card><CardContent className="grid gap-3 py-4 text-sm sm:grid-cols-2">
        <Info label="Feature set" value={`${e.featureSetVersion} · ${r.featureNames.length} features`} />
        <Info label="Selected hyperparameters" value={Object.entries(r.selectedHyperparameters).map(([k, v]) => `${k}=${v}`).join(', ')} />
        <Info label="Data" value={`${date(e.historyStart)} – ${date(e.historyEnd)} · snapshot ${e.dataSnapshotHash ?? '—'}`} />
        <Info label="Samples" value={`${num(r.totalSamples, 0)} total · ${num(r.droppedSamples, 0)} dropped for missing features`} />
        <Info label="Duration" value={e.durationMs ? `${num(e.durationMs / 1000, 1)} s` : '—'} />
        <Info label="Universe" value={e.symbols.join(', ')} />
        <Info label="Features" value={r.featureNames.join(', ')} />
      </CardContent></Card>
      <Card><CardContent className="py-4">
        <SectionTitle hint="Artifacts are stored outside the database and verified by SHA-256 before loading. The evaluation model produced the out-of-sample metrics; the deployment model (all realized labels) produces current predictions.">Model versions</SectionTitle>
        <table className="w-full text-xs tabular">
          <thead><tr className="text-left text-muted-foreground"><th className="py-1">Version</th><th>Role</th><th>Trained</th><th>Samples</th><th>SHA-256</th><th>Size</th></tr></thead>
          <tbody>{e.modelVersions.map((v) => (
            <tr key={v.id} className="border-t"><td className="py-1">v{v.version}</td><td>{v.role}</td><td>{date(v.trainedFrom)} – {date(v.trainedThrough)}</td><td>{num(v.trainingSamples, 0)}</td>
              <td><code title={v.artifactSha256}>{v.artifactSha256.slice(0, 12)}…</code></td><td>{num(v.artifactSizeBytes / 1024, 0)} KB</td></tr>
          ))}</tbody>
        </table>
      </CardContent></Card>
      {r.eligibility && r.eligibility.length > 0 && <EligibilityTable rows={r.eligibility} />}
    </>
  )
}

function EligibilityTable({ rows }: { rows: AssetEligibility[] }) {
  const dash = (v: number | null) => (v === null ? '—' : num(v, 0))
  return (
    <Card><CardContent className="py-4">
      <SectionTitle hint="Each stock's path: stored prices in the period → prices within the benchmark's stored span (the experiment's calendar) → prices without an exchange rate → usable prices → samples (decision dates with enough features; labeled = horizon elapsed) → latest prediction and weight. The stage is the last step reached.">Stock eligibility</SectionTitle>
      <div className="overflow-x-auto">
        <table className="w-full text-xs tabular">
          <thead><tr className="text-left text-muted-foreground">
            <th className="py-1">Stock</th><th>Stored</th><th>In span</th><th>No FX rate</th><th>Usable</th><th>Samples</th><th>Labeled</th><th>Rank</th><th>Weight</th><th>Stage</th>
          </tr></thead>
          <tbody>{rows.map((a) => (
            <tr key={a.symbol} className="border-t align-top">
              <td className="py-1 font-medium">{a.symbol}</td><td>{num(a.storedPrices, 0)}</td><td>{num(a.pricesInBenchmarkSpan, 0)}</td>
              <td>{num(a.pricesWithoutFx, 0)}</td><td>{num(a.usablePrices, 0)}</td><td>{dash(a.samples)}</td><td>{dash(a.labeledSamples)}</td>
              <td>{dash(a.rank)}</td><td>{a.targetWeight > 0 ? `${num(a.targetWeight * 100, 1)} %` : '—'}</td>
              <td><div>{a.stage}</div>{a.reason && <div className="text-muted-foreground">{a.reason}</div>}</td>
            </tr>
          ))}</tbody>
        </table>
      </div>
    </CardContent></Card>
  )
}

function Info({ label, value }: { label: string; value: string }) {
  return <div><div className="text-xs text-muted-foreground">{label}</div><div className="break-words">{value}</div></div>
}
