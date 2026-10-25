import { useEffect, useState, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { Controller, useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { toast } from 'sonner'
import { AlertTriangle, BrainCircuit, Loader2, Play, Sigma } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Progress } from '@/components/ui/progress'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { ChartCard, TimeSeriesChart, WeightsOverTimeChart, seriesColor, timeSeriesTable } from '@/components/charts'
import { DataTable } from '@/components/data-table'
import { Collapsible, DataBadge, EmptyState, ErrorState, LoadingBlock, MetricValue, PageHeader, Warnings } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { portfolioPath, usePortfolioContext } from '@/lib/portfolio-context'
import { useAssets, useBacktest, useBacktests, useCreateBacktest, useCreateStrategyBacktest, usePortfolio, usePortfolioStrategies } from '@/lib/queries'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { NoStrategies, PortfolioPicker, StrategySelect } from '@/components/strategy-picker'
import { dataNeeded, strategyKey, strategyRef } from '@/lib/strategies'
import { useMissingDataGuard } from '@/components/missing-data-dialog'
import { lookbackStart } from '@/lib/data-check'
import type { Backtest } from '@/lib/types'
import { date, dateTime, money, num, objectiveLabels, pct } from '@/lib/format'

const schema = z.object({
  name: z.string().max(100).optional(),
  universe: z.array(z.string()).min(1, 'Select at least one asset'),
  startDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, 'Required'),
  endDate: z.string().regex(/^\d{4}-\d{2}-\d{2}$/, 'Required'),
  initialCapital: z.number().min(100),
  rebalanceFrequency: z.enum(['None', 'Monthly', 'Quarterly']),
  objective: z.enum(['MinimumVariance', 'MaximumSharpe', 'TargetReturn', 'MaximumReturnForRisk', 'EqualWeight']),
  lookbackDays: z.number().int().min(60).max(2520),
  transactionCostBps: z.number().min(0).max(500),
  riskFreeRate: z.number().min(-5).max(25),
  benchmarkSymbol: z.string(),
  maxWeight: z.number().min(1).max(100),
  targetReturn: z.number().optional(),
  maxVolatility: z.number().optional(),
}).superRefine((v, ctx) => {
  if (v.endDate <= v.startDate) ctx.addIssue({ code: 'custom', path: ['endDate'], message: 'Must be after the start date' })
  if (v.objective === 'TargetReturn' && v.targetReturn === undefined) ctx.addIssue({ code: 'custom', path: ['targetReturn'], message: 'Required' })
  if (v.objective === 'MaximumReturnForRisk' && v.maxVolatility === undefined) ctx.addIssue({ code: 'custom', path: ['maxVolatility'], message: 'Required' })
  if ((v.maxWeight / 100) * v.universe.length < 1) ctx.addIssue({ code: 'custom', path: ['maxWeight'], message: 'Too low to be fully invested with this universe' })
})
type FormValues = z.infer<typeof schema>
const asNum = { setValueAs: (v: unknown) => (v === '' || v === null || v === undefined ? undefined : Number(v)) }

/** Where backtests open: inside a portfolio's workspace (`/portfolios/:id/backtest`) or on the page of all backtests. */
function backtestBase(portfolioId: string | undefined) {
  return portfolioId ? portfolioPath(portfolioId, 'backtest') : '/backtest'
}

/**
 * Backtests. Inside a portfolio (`/portfolios/:portfolioId/backtest`) only that portfolio's backtests are listed and new ones
 * belong to it; `/backtest` lists all of the user's backtests and lets the user choose the portfolio.
 */
export function BacktestPage() {
  const { backtestId, portfolioId: scoped } = useParams()
  const list = useBacktests(scoped)
  const activeId = backtestId ?? list.data?.items[0]?.id
  const bt = useBacktest(activeId)
  const base = backtestBase(scoped)

  return (
    <>
      {scoped
        ? <PageHeader level={2} title="Backtest" info="Walk-forward historical simulation of this portfolio's Math and ML Optimizations – or of a custom strategy – with periodic rebalancing, transaction costs and benchmark comparison." />
        : <PageHeader title="Backtesting" info="Walk-forward historical simulation of any optimization – Math or ML – or of a custom strategy, with periodic rebalancing, transaction costs and benchmark comparison." />}
      <Alert className="mb-6">
        <AlertTriangle className="text-warn" />
        <AlertTitle>Hypothetical results</AlertTitle>
        <AlertDescription>Backtests use today’s list of symbols (survivorship bias) and historical estimates. They are not forecasts and do not guarantee future returns.</AlertDescription>
      </Alert>
      <div className="grid gap-6 xl:grid-cols-[360px_minmax(0,1fr)]">
        <div className="space-y-4">
          <StrategyBacktestCard portfolioId={scoped} />
          <BacktestForm portfolioId={scoped} />
          {list.data && list.data.items.length > 0 && (
            <Card>
              <CardHeader><CardTitle className="text-sm">Previous backtests</CardTitle></CardHeader>
              <CardContent className="max-h-72 space-y-1 overflow-y-auto">
                {list.data.items.map((b) => (
                  <Link key={b.id} to={`${base}/${b.id}`} className={`block rounded-md px-2.5 py-1.5 text-sm hover:bg-muted ${b.id === activeId ? 'bg-muted' : ''}`}>
                    <div className="flex items-center justify-between gap-2"><span className="truncate font-medium">{b.name}</span><StatusBadge status={b.status} /></div>
                    {b.strategyKind && <div className="text-[11px] text-muted-foreground">{b.strategyKind === 'MachineLearning' ? 'ML Optimization' : 'Math Optimization'}</div>}
                    <div className="text-xs text-muted-foreground">{dateTime(b.createdAtUtc)}</div>
                  </Link>
                ))}
              </CardContent>
            </Card>
          )}
        </div>
        <div className="min-w-0">
          {bt.isLoading || list.isLoading ? <LoadingBlock height="h-96" /> : bt.error ? <ErrorState error={bt.error} /> : bt.data ? <BacktestResults bt={bt.data} /> : (
            <EmptyState title="No backtests yet" description={scoped ? 'Backtest one of this portfolio’s optimizations or a custom strategy on the left.' : 'Configure a strategy on the left and run it.'} />
          )}
        </div>
      </div>
    </>
  )
}

const isoDate = (d: Date) => d.toISOString().slice(0, 10)

/** Opens the window that chooses which optimization of which portfolio is backtested. */
function StrategyBacktestCard({ portfolioId }: { portfolioId?: string }) {
  const [open, setOpen] = useState(false)
  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-sm">Backtest an optimization</CardTitle>
        <CardDescription className="text-xs">
          Any Math or ML Optimization of a portfolio, re-simulated walk-forward over a period you choose with its stored settings.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <Button className="w-full" variant="outline" onClick={() => setOpen(true)}><Sigma /> <BrainCircuit /> Choose optimization…</Button>
        {open && <StrategyBacktestDialog fixedPortfolioId={portfolioId} onClose={() => setOpen(false)} />}
      </CardContent>
    </Card>
  )
}

/** `fixedPortfolioId`: opened inside a portfolio – its optimizations only, no portfolio choice. */
function StrategyBacktestDialog({ fixedPortfolioId, onClose }: { fixedPortfolioId?: string; onClose: () => void }) {
  const { portfolioId: defaultPortfolio, portfolios } = usePortfolioContext()
  const [portfolioId, setPortfolioId] = useState<string | undefined>(fixedPortfolioId ?? defaultPortfolio)
  const strategies = usePortfolioStrategies(portfolioId)
  const create = useCreateStrategyBacktest()
  const missingData = useMissingDataGuard()
  const navigate = useNavigate()
  const [key, setKey] = useState<string | null>(null)
  const [end, setEnd] = useState(() => isoDate(new Date()))
  const [start, setStart] = useState(() => { const d = new Date(); d.setFullYear(d.getFullYear() - 3); return isoDate(d) })
  const [capital, setCapital] = useState(100_000)
  const [costs, setCosts] = useState(10)
  const [rebalance, setRebalance] = useState<'Monthly' | 'Quarterly'>('Monthly')
  const list = strategies.data ?? []
  const chosen = list.find((s) => strategyKey(s) === key)
  const portfolio = portfolios.find((p) => p.id === portfolioId)

  const submit = () => {
    const ref = strategyRef(key, list)
    if (!portfolioId || !ref || !chosen || !portfolio) return
    return missingData.guard(dataNeeded([chosen], start, end, portfolio.baseCurrency), async () => {
      try {
        const b = await create.mutateAsync({ portfolioId, strategy: ref, startDate: start, endDate: end, initialCapital: capital, transactionCostBps: costs, rebalanceFrequency: rebalance })
        toast.success('Backtest queued', { description: 'It runs in the background; results appear automatically.' })
        onClose()
        navigate(`${backtestBase(fixedPortfolioId)}/${b.id}`)
      } catch (e) {
        toast.error('Could not start backtest', { description: errorMessage(e) })
      }
    })
  }

  return (
    <Dialog open onOpenChange={(o) => { if (!o) onClose() }}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Backtest an optimization</DialogTitle>
          <DialogDescription>
            Choose the portfolio and one of its optimizations. A Math Optimization is re-optimized with its settings at every rebalance; an ML
            Optimization is retrained walk-forward with its selected hyperparameters. Results before an ML experiment's test period are marked as partly in-sample.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-3">
          {fixedPortfolioId
            ? <p className="text-xs text-muted-foreground">Portfolio: <span className="font-medium text-foreground">{portfolio?.name}</span> ({portfolio?.baseCurrency})</p>
            : <PortfolioPicker value={portfolioId} onChange={(id) => { setPortfolioId(id); setKey(null) }} id="bt-portfolio" />}
          {portfolioId && (strategies.error ? <ErrorState error={strategies.error} /> : !strategies.data ? <LoadingBlock height="h-16" /> : list.length === 0
            ? <NoStrategies portfolioId={portfolioId} />
            : <StrategySelect strategies={list} value={key} onChange={setKey} label="Optimization" />)}
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5"><Label htmlFor="bt-s-start" className="text-xs">Start</Label><Input id="bt-s-start" type="date" value={start} onChange={(e) => setStart(e.target.value)} /></div>
            <div className="space-y-1.5"><Label htmlFor="bt-s-end" className="text-xs">End</Label><Input id="bt-s-end" type="date" value={end} onChange={(e) => setEnd(e.target.value)} /></div>
            <div className="space-y-1.5"><Label htmlFor="bt-s-capital" className="text-xs">Initial capital</Label><Input id="bt-s-capital" type="number" value={capital} min={100} step={1000} onChange={(e) => setCapital(Number(e.target.value))} /></div>
            <div className="space-y-1.5"><Label htmlFor="bt-s-costs" className="text-xs">Transaction cost (bps)</Label><Input id="bt-s-costs" type="number" value={costs} min={0} max={500} onChange={(e) => setCosts(Number(e.target.value))} /></div>
            <div className="space-y-1.5">
              <Label className="text-xs">Rebalancing</Label>
              <Select value={rebalance} onValueChange={(v) => setRebalance(v as 'Monthly' | 'Quarterly')} items={[{ value: 'Monthly', label: 'Monthly' }, { value: 'Quarterly', label: 'Quarterly' }]}>
                <SelectTrigger className="w-full" aria-label="Rebalancing"><SelectValue /></SelectTrigger>
                <SelectContent><SelectItem value="Monthly">Monthly</SelectItem><SelectItem value="Quarterly">Quarterly</SelectItem></SelectContent>
              </Select>
            </div>
          </div>
          {end <= start && <p className="text-xs text-destructive">The end must be after the start.</p>}
        </div>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button onClick={submit} disabled={!chosen || end <= start || create.isPending || missingData.checking}>
            {create.isPending || missingData.checking ? <Loader2 className="animate-spin" /> : <Play />} Run backtest
          </Button>
        </DialogFooter>
        {missingData.dialog}
      </DialogContent>
    </Dialog>
  )
}

function StatusBadge({ status }: { status: string }) {
  return <Badge variant={status === 'Failed' ? 'destructive' : status === 'Succeeded' ? 'secondary' : 'outline'} className="text-[10px]">{status}</Badge>
}

/** Custom strategy. Inside a portfolio the backtest belongs to it; elsewhere it uses the current portfolio's settings as defaults only. */
function BacktestForm({ portfolioId: scoped }: { portfolioId?: string }) {
  const assets = useAssets()
  const { portfolioId: current } = usePortfolioContext()
  const portfolioId = scoped ?? current
  const portfolio = usePortfolio(portfolioId)
  const create = useCreateBacktest()
  const navigate = useNavigate()
  const missingData = useMissingDataGuard()
  const today = new Date().toISOString().slice(0, 10)
  const { register, control, handleSubmit, watch, reset, formState: { errors } } = useForm<FormValues>({
    resolver: zodResolver(schema),
    defaultValues: {
      universe: [], startDate: `${new Date().getFullYear() - 3}-01-01`, endDate: today, initialCapital: 100000, rebalanceFrequency: 'Monthly',
      objective: 'MaximumSharpe', lookbackDays: 252, transactionCostBps: 5, riskFreeRate: 4, benchmarkSymbol: 'SPY', maxWeight: 35,
    },
  })
  useEffect(() => {
    const s = portfolio.data?.settings
    if (s) reset((v) => ({ ...v, universe: s.universe, riskFreeRate: s.riskFreeRate * 100, benchmarkSymbol: s.benchmarkSymbol, maxWeight: s.maxWeight * 100, objective: s.objective, transactionCostBps: s.transactionCostBps }))
  }, [portfolio.data, reset])
  const objective = watch('objective')
  const enabled = (assets.data ?? []).filter((a) => a.isEnabled)

  // Missing prices or exchange rates for the period (incl. the lookback before it) are offered for download first.
  const onSubmit = handleSubmit((v) => missingData.guard({
    symbols: v.universe, benchmarkSymbol: v.benchmarkSymbol || null, from: lookbackStart(new Date(v.startDate), v.lookbackDays), to: v.endDate,
    currency: portfolio.data?.baseCurrency ?? null,
  }, () => start(v)))
  const start = async (v: FormValues) => {
    try {
      const b = await create.mutateAsync({
        name: v.name || undefined, universe: v.universe, startDate: v.startDate, endDate: v.endDate, initialCapital: v.initialCapital,
        rebalanceFrequency: v.rebalanceFrequency, objective: v.objective, covarianceMethod: 'LedoitWolf', lookbackDays: v.lookbackDays,
        transactionCostBps: v.transactionCostBps, riskFreeRate: v.riskFreeRate / 100, benchmarkSymbol: v.benchmarkSymbol || null,
        minWeight: 0, maxWeight: v.maxWeight / 100, cashReserve: 0, maxHoldings: null, currency: portfolio.data?.baseCurrency ?? null,
        portfolioId: scoped ?? null,
        targetReturn: v.targetReturn !== undefined ? v.targetReturn / 100 : null, maxVolatility: v.maxVolatility !== undefined ? v.maxVolatility / 100 : null,
      })
      toast.success('Backtest queued', { description: 'It runs in the background; results appear automatically.' })
      navigate(`${backtestBase(scoped)}/${b.id}`)
    } catch (e) {
      toast.error('Could not start backtest', { description: errorMessage(e) })
    }
  }

  if (!assets.data) return <LoadingBlock height="h-[560px]" />
  return (
    <Card>
      <CardHeader><CardTitle className="text-sm">Custom strategy</CardTitle><CardDescription className="text-xs">Mean-variance settings entered here. Defaults are taken from the selected portfolio.</CardDescription></CardHeader>
      <CardContent>
        <form onSubmit={onSubmit} className="space-y-4" noValidate>
          <Controller control={control} name="universe" render={({ field }) => (
            <fieldset>
              <legend className="mb-1.5 text-xs font-medium">Universe ({field.value.length})</legend>
              <div className="grid max-h-40 grid-cols-3 gap-1 overflow-y-auto rounded-md border p-2">
                {enabled.map((a) => (
                  <label key={a.symbol} className="flex cursor-pointer items-center gap-1.5 text-sm">
                    <Checkbox checked={field.value.includes(a.symbol)} onCheckedChange={(on) => field.onChange(on ? [...field.value, a.symbol] : field.value.filter((s) => s !== a.symbol))} aria-label={a.symbol} />
                    {a.symbol}
                  </label>
                ))}
              </div>
              {errors.universe && <p className="mt-1 text-xs text-destructive">{errors.universe.message}</p>}
            </fieldset>
          )} />
          <div className="grid grid-cols-2 gap-3">
            <F label="Start date" error={errors.startDate?.message}><Input type="date" {...register('startDate')} /></F>
            <F label="End date" error={errors.endDate?.message}><Input type="date" max={today} {...register('endDate')} /></F>
            <F label="Initial capital" error={errors.initialCapital?.message}><Input type="number" {...register('initialCapital', asNum)} /></F>
            <F label="Rebalancing">
              <Controller control={control} name="rebalanceFrequency" render={({ field }) => (
                <Select value={field.value} onValueChange={field.onChange} items={[{ value: 'Monthly', label: 'Monthly' }, { value: 'Quarterly', label: 'Quarterly' }, { value: 'None', label: 'Buy & hold' }]}>
                  <SelectTrigger className="w-full" aria-label="Rebalancing"><SelectValue /></SelectTrigger>
                  <SelectContent><SelectItem value="Monthly">Monthly</SelectItem><SelectItem value="Quarterly">Quarterly</SelectItem><SelectItem value="None">Buy & hold</SelectItem></SelectContent>
                </Select>
              )} />
            </F>
          </div>
          <F label="Objective">
            <Controller control={control} name="objective" render={({ field }) => (
              <Select value={field.value} onValueChange={field.onChange} items={Object.entries(objectiveLabels).map(([value, label]) => ({ value, label }))}>
                <SelectTrigger className="w-full" aria-label="Objective"><SelectValue /></SelectTrigger>
                <SelectContent>{Object.entries(objectiveLabels).map(([k, l]) => <SelectItem key={k} value={k}>{l}</SelectItem>)}</SelectContent>
              </Select>
            )} />
          </F>
          <div className="grid grid-cols-2 gap-3">
            {objective === 'TargetReturn' && <F label="Target return (%)" error={errors.targetReturn?.message}><Input type="number" step="any" {...register('targetReturn', asNum)} /></F>}
            {objective === 'MaximumReturnForRisk' && <F label="Volatility limit (%)" error={errors.maxVolatility?.message}><Input type="number" step="any" {...register('maxVolatility', asNum)} /></F>}
            <F label="Lookback (days)" error={errors.lookbackDays?.message}><Input type="number" {...register('lookbackDays', asNum)} /></F>
            <F label="Max weight (%)" error={errors.maxWeight?.message}><Input type="number" step="any" {...register('maxWeight', asNum)} /></F>
            <F label="Costs (bps)" error={errors.transactionCostBps?.message}><Input type="number" step="any" {...register('transactionCostBps', asNum)} /></F>
            <F label="Risk-free (%)" error={errors.riskFreeRate?.message}><Input type="number" step="any" {...register('riskFreeRate', asNum)} /></F>
            <F label="Benchmark">
              <Controller control={control} name="benchmarkSymbol" render={({ field }) => (
                <Select value={field.value} onValueChange={(v) => field.onChange(String(v))} items={enabled.map((a) => ({ value: a.symbol, label: a.symbol }))}>
                  <SelectTrigger className="w-full" aria-label="Benchmark"><SelectValue /></SelectTrigger>
                  <SelectContent>{enabled.map((a) => <SelectItem key={a.symbol} value={a.symbol}>{a.symbol}</SelectItem>)}</SelectContent>
                </Select>
              )} />
            </F>
            <F label="Name (optional)"><Input {...register('name')} placeholder="Auto" /></F>
          </div>
          <Button type="submit" className="w-full" disabled={create.isPending || missingData.checking}>{create.isPending || missingData.checking ? <Loader2 className="animate-spin" /> : <Play />} Run backtest</Button>
        </form>
        {missingData.dialog}
      </CardContent>
    </Card>
  )
}

function F({ label, error, children }: { label: string; error?: string; children: ReactNode }) {
  return (
    <div className="space-y-1.5">
      <Label className="text-xs">{label}</Label>
      {children}
      {error && <p role="alert" className="text-xs text-destructive">{error}</p>}
    </div>
  )
}

function BacktestResults({ bt }: { bt: Backtest }) {
  const r = bt.result
  // Every amount is in the backtest's analysis currency (the portfolio's), stored with its configuration.
  const ccy = bt.config.currency ?? 'USD'
  const running = bt.status === 'Queued' || bt.status === 'Running'
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-lg font-semibold">{bt.name}</h2>
        <StatusBadge status={bt.status} />
        <DataBadge kind="backtest" />
        {bt.usesSimulatedData && <DataBadge kind="simulated" />}
        <span className="text-xs text-muted-foreground">{date(bt.config.startDate)} – {date(bt.config.endDate)} · {bt.config.strategy ? `${bt.config.strategy.kind === 'MachineLearning' ? 'ML' : 'Math'}: ${bt.config.strategy.label}` : objectiveLabels[bt.config.objective]} · {bt.config.rebalanceFrequency === 'None' ? 'buy & hold' : bt.config.rebalanceFrequency.toLowerCase()}</span>
      </div>

      {running && (
        <Card><CardContent className="space-y-2 py-6 text-center text-sm text-muted-foreground"><Loader2 className="mx-auto size-6 animate-spin" />Running in the background…<Progress value={null} className="mx-auto max-w-xs" /></CardContent></Card>
      )}
      {bt.status === 'Failed' && <Alert variant="destructive"><AlertTriangle /><AlertTitle>Backtest failed</AlertTitle><AlertDescription>{bt.error}</AlertDescription></Alert>}

      {r && (
        <>
          <DataTable
            caption="Backtest summary"
            rows={r.metrics}
            rowKey={(m) => m.name}
            dense
            columns={[
              { key: 'n', header: 'Series', cell: (m) => <span className="flex items-center gap-2 font-medium"><span className="inline-block size-2.5 rounded-sm" style={{ background: seriesColor(r.metrics.indexOf(m)) }} aria-hidden />{m.name}</span> },
              { key: 't', header: 'Total return', align: 'right', cell: (m) => pct(m.totalReturn, 1, true) },
              { key: 'a', header: 'CAGR', align: 'right', cell: (m) => <MetricValue metric={m.annualizedReturn} format={(v) => pct(v)} /> },
              { key: 'v', header: 'Volatility', align: 'right', cell: (m) => <MetricValue metric={m.annualizedVolatility} format={(v) => pct(v)} /> },
              { key: 's', header: 'Sharpe', align: 'right', cell: (m) => <MetricValue metric={m.sharpeRatio} format={(v) => num(v)} /> },
              { key: 'so', header: 'Sortino', align: 'right', cell: (m) => <MetricValue metric={m.sortinoRatio} format={(v) => num(v)} /> },
              { key: 'd', header: 'Max DD', align: 'right', cell: (m) => <MetricValue metric={m.maxDrawdown} format={(v) => pct(v)} /> },
              { key: 'to', header: 'Turnover', align: 'right', cell: (m) => pct(m.totalTurnover, 0) },
              { key: 'c', header: 'Costs', align: 'right', cell: (m) => money(m.totalTransactionCosts, ccy, { decimals: 0 }) },
            ]}
          />
          <div className="flex flex-wrap gap-4 text-xs text-muted-foreground">
            <span>Strategy beta: <MetricValue metric={r.beta} format={(v) => num(v)} /></span>
            <span>Tracking error: <MetricValue metric={r.trackingError} format={(v) => pct(v)} /></span>
            <span>Information ratio: <MetricValue metric={r.informationRatio} format={(v) => num(v)} /></span>
          </div>

          <ChartCard
            title="Equity curve"
            description={`Portfolio value (${ccy}), ${date(r.dates[0])} – ${date(r.dates[r.dates.length - 1])}`}
            legend={Object.keys(r.values).map((k, i) => ({ name: k, color: seriesColor(i), dashed: k.startsWith('Benchmark') }))}
            table={timeSeriesTable(r.dates, Object.entries(r.values).map(([name, values]) => ({ name, values })), (v) => money(v, ccy, { decimals: 0 }))}
          >
            <TimeSeriesChart dates={r.dates} series={Object.entries(r.values).map(([name, values]) => ({ name, values, dashed: name.startsWith('Benchmark') }))} format={(v) => money(v, ccy, { compact: true })} height={320} />
          </ChartCard>

          <ChartCard title="Drawdown" description="Decline from running peak, %"
            legend={Object.keys(r.drawdowns).map((k, i) => ({ name: k, color: seriesColor(i), dashed: k.startsWith('Benchmark') }))}
            table={timeSeriesTable(r.dates, Object.entries(r.drawdowns).map(([name, values]) => ({ name, values })), (v) => pct(v))}>
            <TimeSeriesChart dates={r.dates} series={Object.entries(r.drawdowns).map(([name, values]) => ({ name, values, dashed: name.startsWith('Benchmark') }))} format={(v) => pct(v, 0)} height={220} yDomain={['dataMin', 0]} />
          </ChartCard>

          {r.rebalances.length > 1 && (
            <Card>
              <CardHeader><CardTitle className="text-sm">Target weights at each rebalance</CardTitle><CardDescription className="text-xs">Set at the signal date’s close, executed at the next trading day’s close.</CardDescription></CardHeader>
              <CardContent><WeightsOverTimeChart points={r.rebalances.filter((e) => e.optimizationSucceeded).map((e) => ({ date: e.executionDate, weights: e.weights }))} /></CardContent>
            </Card>
          )}

          <MonthlyReturnsChart months={r.monthlyReturns} />

          <Collapsible title="Rebalance log" summary={`${r.rebalances.length} rebalances`}
            hint="Invested: sum of the target weights; the rest is cash (cash reserve, or a stock without a price on the execution day). “Held”: no new target – the optimization failed or no stock had a full lookback window, so the previous holdings were kept.">
            <DataTable
              caption="Rebalance log"
              rows={r.rebalances}
              rowKey={(e) => e.signalDate}
              dense
              columns={[
                { key: 's', header: 'Signal', cell: (e) => e.signalDate },
                { key: 'e', header: 'Executed', cell: (e) => e.executionDate },
                { key: 'n', header: 'Assets', align: 'right', cell: (e) => `${Object.keys(e.weights).length}/${e.eligibleSymbols.length}` },
                { key: 'i', header: 'Invested', align: 'right', cell: (e) => (!e.optimizationSucceeded && Object.keys(e.weights).length === 0 ? <span className="text-warn">held</span> : pct(Object.values(e.weights).reduce((a, w) => a + w, 0), 1)) },
                { key: 't', header: 'Turnover', align: 'right', cell: (e) => pct(e.turnover, 1) },
                { key: 'c', header: 'Costs', align: 'right', cell: (e) => money(e.transactionCosts, ccy) },
                { key: 'v', header: 'Value', align: 'right', cell: (e) => money(e.portfolioValue, ccy, { decimals: 0 }) },
                { key: 'm', header: 'Note', cell: (e) => <span className={e.optimizationSucceeded ? 'text-xs text-muted-foreground' : 'text-xs text-warn'}>{e.message === 'OK' ? '' : e.message}</span> },
              ]}
            />
          </Collapsible>
          <Warnings items={r.warnings} collapsible />
          <p className="text-xs leading-relaxed text-muted-foreground">{r.methodology}</p>
        </>
      )}
    </div>
  )
}

/** Monthly returns of the strategy and its benchmark as lines (the table view lists every month). */
function MonthlyReturnsChart({ months }: { months: { year: number; month: number; portfolio: number | null; benchmark: number | null }[] }) {
  if (months.length === 0) return null
  const dates = months.map((m) => `${m.year}-${String(m.month).padStart(2, '0')}-01`)
  const series = [
    { name: 'Strategy', color: seriesColor(0), values: months.map((m) => m.portfolio) },
    ...(months.some((m) => m.benchmark !== null) ? [{ name: 'Benchmark', color: seriesColor(3), dashed: true, values: months.map((m) => m.benchmark) }] : []),
  ]
  return (
    <ChartCard title="Monthly returns" description="Return of each calendar month (the first and last month may be partial)"
      legend={series.map((x) => ({ name: x.name, color: x.color, dashed: x.dashed }))} table={timeSeriesTable(dates, series, (v) => pct(v, 1))}>
      <TimeSeriesChart dates={dates} series={series} format={(v) => pct(v, 0)} height={260} zeroLine />
    </ChartCard>
  )
}
