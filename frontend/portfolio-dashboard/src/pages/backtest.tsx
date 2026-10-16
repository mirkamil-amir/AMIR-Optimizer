import { useEffect, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router'
import { Controller, useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { toast } from 'sonner'
import { AlertTriangle, Loader2, Play } from 'lucide-react'
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Checkbox } from '@/components/ui/checkbox'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Progress } from '@/components/ui/progress'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { ChartCard, MonthlyReturnsGrid, TimeSeriesChart, WeightsOverTimeChart, seriesColor, timeSeriesTable } from '@/components/charts'
import { DataTable } from '@/components/data-table'
import { DataBadge, EmptyState, ErrorState, LoadingBlock, MetricValue, PageHeader, Warnings } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { usePortfolioContext } from '@/lib/portfolio-context'
import { useAssets, useBacktest, useBacktests, useCreateBacktest, usePortfolio } from '@/lib/queries'
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

export function BacktestPage() {
  const { backtestId } = useParams()
  const list = useBacktests()
  const activeId = backtestId ?? list.data?.items[0]?.id
  const bt = useBacktest(activeId)

  return (
    <>
      <PageHeader title="Backtesting" description="Walk-forward historical simulation with periodic re-optimization, transaction costs and benchmark comparison." />
      <Alert className="mb-6">
        <AlertTriangle className="text-warn" />
        <AlertTitle>Hypothetical results</AlertTitle>
        <AlertDescription>Backtests use today’s list of symbols (survivorship bias) and historical estimates. They are not forecasts and do not guarantee future returns.</AlertDescription>
      </Alert>
      <div className="grid gap-6 xl:grid-cols-[360px_minmax(0,1fr)]">
        <div className="space-y-4">
          <BacktestForm />
          {list.data && list.data.items.length > 0 && (
            <Card>
              <CardHeader><CardTitle className="text-sm">Previous backtests</CardTitle></CardHeader>
              <CardContent className="max-h-72 space-y-1 overflow-y-auto">
                {list.data.items.map((b) => (
                  <Link key={b.id} to={`/backtest/${b.id}`} className={`block rounded-md px-2.5 py-1.5 text-sm hover:bg-muted ${b.id === activeId ? 'bg-muted' : ''}`}>
                    <div className="flex items-center justify-between gap-2"><span className="truncate font-medium">{b.name}</span><StatusBadge status={b.status} /></div>
                    <div className="text-xs text-muted-foreground">{dateTime(b.createdAtUtc)}</div>
                  </Link>
                ))}
              </CardContent>
            </Card>
          )}
        </div>
        <div className="min-w-0">
          {bt.isLoading || list.isLoading ? <LoadingBlock height="h-96" /> : bt.error ? <ErrorState error={bt.error} /> : bt.data ? <BacktestResults bt={bt.data} /> : (
            <EmptyState title="No backtests yet" description="Configure a strategy on the left and run it." />
          )}
        </div>
      </div>
    </>
  )
}

function StatusBadge({ status }: { status: string }) {
  return <Badge variant={status === 'Failed' ? 'destructive' : status === 'Succeeded' ? 'secondary' : 'outline'} className="text-[10px]">{status}</Badge>
}

function BacktestForm() {
  const assets = useAssets()
  const { portfolioId } = usePortfolioContext()
  const portfolio = usePortfolio(portfolioId)
  const create = useCreateBacktest()
  const navigate = useNavigate()
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

  const onSubmit = handleSubmit(async (v) => {
    try {
      const b = await create.mutateAsync({
        name: v.name || undefined, universe: v.universe, startDate: v.startDate, endDate: v.endDate, initialCapital: v.initialCapital,
        rebalanceFrequency: v.rebalanceFrequency, objective: v.objective, covarianceMethod: 'LedoitWolf', lookbackDays: v.lookbackDays,
        transactionCostBps: v.transactionCostBps, riskFreeRate: v.riskFreeRate / 100, benchmarkSymbol: v.benchmarkSymbol || null,
        minWeight: 0, maxWeight: v.maxWeight / 100, cashReserve: 0, maxHoldings: null, currency: portfolio.data?.baseCurrency ?? null,
        targetReturn: v.targetReturn !== undefined ? v.targetReturn / 100 : null, maxVolatility: v.maxVolatility !== undefined ? v.maxVolatility / 100 : null,
      })
      toast.success('Backtest queued', { description: 'It runs in the background; results appear automatically.' })
      navigate(`/backtest/${b.id}`)
    } catch (e) {
      toast.error('Could not start backtest', { description: errorMessage(e) })
    }
  })

  if (!assets.data) return <LoadingBlock height="h-[560px]" />
  return (
    <Card>
      <CardHeader><CardTitle className="text-sm">Strategy</CardTitle><CardDescription className="text-xs">Defaults are taken from the selected portfolio.</CardDescription></CardHeader>
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
          <Button type="submit" className="w-full" disabled={create.isPending}>{create.isPending ? <Loader2 className="animate-spin" /> : <Play />} Run backtest</Button>
        </form>
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
  const running = bt.status === 'Queued' || bt.status === 'Running'
  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-2">
        <h2 className="text-lg font-semibold">{bt.name}</h2>
        <StatusBadge status={bt.status} />
        <DataBadge kind="backtest" />
        {bt.usesSimulatedData && <DataBadge kind="simulated" />}
        <span className="text-xs text-muted-foreground">{date(bt.config.startDate)} – {date(bt.config.endDate)} · {objectiveLabels[bt.config.objective]} · {bt.config.rebalanceFrequency === 'None' ? 'buy & hold' : bt.config.rebalanceFrequency.toLowerCase()}</span>
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
              { key: 'c', header: 'Costs', align: 'right', cell: (m) => money(m.totalTransactionCosts, 'USD', { decimals: 0 }) },
            ]}
          />
          <div className="flex flex-wrap gap-4 text-xs text-muted-foreground">
            <span>Strategy beta: <MetricValue metric={r.beta} format={(v) => num(v)} /></span>
            <span>Tracking error: <MetricValue metric={r.trackingError} format={(v) => pct(v)} /></span>
            <span>Information ratio: <MetricValue metric={r.informationRatio} format={(v) => num(v)} /></span>
          </div>

          <ChartCard
            title="Equity curve"
            description={`Portfolio value (USD), ${date(r.dates[0])} – ${date(r.dates[r.dates.length - 1])}`}
            legend={Object.keys(r.values).map((k, i) => ({ name: k, color: seriesColor(i), dashed: k.startsWith('Benchmark') }))}
            table={timeSeriesTable(r.dates, Object.entries(r.values).map(([name, values]) => ({ name, values })), (v) => money(v, 'USD', { decimals: 0 }))}
          >
            <TimeSeriesChart dates={r.dates} series={Object.entries(r.values).map(([name, values]) => ({ name, values, dashed: name.startsWith('Benchmark') }))} format={(v) => money(v, 'USD', { compact: true })} height={320} />
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

          <Card>
            <CardHeader><CardTitle className="text-sm">Monthly returns (strategy)</CardTitle></CardHeader>
            <CardContent><MonthlyReturnsGrid items={r.monthlyReturns.map((m) => ({ year: m.year, month: m.month, value: m.portfolio }))} label="Strategy" /></CardContent>
          </Card>

          <Card>
            <CardHeader><CardTitle className="text-sm">Rebalance log</CardTitle></CardHeader>
            <CardContent>
              <DataTable
                caption="Rebalance log"
                rows={r.rebalances}
                rowKey={(e) => e.signalDate}
                dense
                columns={[
                  { key: 's', header: 'Signal', cell: (e) => e.signalDate },
                  { key: 'e', header: 'Executed', cell: (e) => e.executionDate },
                  { key: 'n', header: 'Assets', align: 'right', cell: (e) => `${Object.keys(e.weights).length}/${e.eligibleSymbols.length}` },
                  { key: 't', header: 'Turnover', align: 'right', cell: (e) => pct(e.turnover, 1) },
                  { key: 'c', header: 'Costs', align: 'right', cell: (e) => money(e.transactionCosts, 'USD') },
                  { key: 'v', header: 'Value', align: 'right', cell: (e) => money(e.portfolioValue, 'USD', { decimals: 0 }) },
                  { key: 'm', header: 'Note', cell: (e) => <span className={e.optimizationSucceeded ? 'text-xs text-muted-foreground' : 'text-xs text-warn'}>{e.message === 'OK' ? '' : e.message}</span> },
                ]}
              />
            </CardContent>
          </Card>
          <Warnings items={r.warnings} />
          <p className="text-xs leading-relaxed text-muted-foreground">{r.methodology}</p>
        </>
      )}
    </div>
  )
}
