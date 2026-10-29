import { useMemo, useState } from 'react'
import { Link, useParams } from 'react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card'
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from '@/components/ui/dialog'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from '@/components/ui/select'
import { Switch } from '@/components/ui/switch'
import { SearchableTable, type Column } from '@/components/data-table'
import { MatrixLoader, WorkingPanel } from '@/components/matrix-loader'
import { DataBadge, ErrorState, LoadingBlock, PageHeader } from '@/components/common'
import { errorMessage } from '@/lib/api'
import { useAuth } from '@/lib/auth'
import { cn } from '@/lib/utils'
import { useCreateAsset, useFxSync, useMarketStatus, useSync, useUpdateAsset } from '@/lib/queries'
import type { AssetCoverage, DataFreshness, FxCoverage } from '@/lib/types'
import { date, dateTime, money, num, relativeTime } from '@/lib/format'
import { describeCatalogue, describeSymbols, trackedTotal } from '@/lib/instrument-counts'

const historyOptions = [
  { value: '1', label: '1 year' },
  { value: '3', label: '3 years' },
  { value: '5', label: '5 years' },
  { value: '10', label: '10 years' },
]

export function MarketDataPage() {
  const { data, isLoading, error, refetch } = useMarketStatus()
  // Provider names, terms and job sources are shown to administrators only.
  const { isAdministrator } = useAuth()
  const sync = useSync()
  const fxSync = useFxSync()
  const update = useUpdateAsset()
  const [years, setYears] = useState('5')
  const [addOpen, setAddOpen] = useState(false)
  const { section = '' } = useParams()
  // Jobs run one at a time: one may be running while others wait in the queue behind it.
  const active = data?.recentJobs.filter((j) => j.status === 'Queued' || j.status === 'Running') ?? []
  const running = active.find((j) => j.status === 'Running') ?? active[0]
  const queued = active.filter((j) => j !== running).length
  // Symbols still waiting for their download (not yet logged by their job): their row shows the loader.
  const downloading = new Set(active.flatMap((j) => j.symbols.filter((s) => !j.logs.some((l) => l.symbol === s))))
  const stocks = useMemo(() => (data?.coverage ?? []).filter((c) => c.assetType !== 'Index'), [data])
  const indexes = useMemo(() => (data?.coverage ?? []).filter((c) => c.assetType === 'Index'), [data])
  const currencies = data?.currencies ?? []
  const current = SECTIONS.find((x) => x.path === section) ?? SECTIONS[0]

  // One set of columns for stocks, ETFs and indexes.
  const columns: Column<AssetCoverage>[] = [
    { key: 'symbol', header: 'Symbol', sortValue: (c) => c.symbol, cell: (c) => <div><div className="font-medium">{c.symbol}</div><div className="max-w-44 truncate text-xs text-muted-foreground">{c.name}</div></div> },
    {
      key: 'status', header: 'Status', sortValue: (c) => c.freshness,
      cell: (c) => c.observations === 0 && !c.isEnabled ? <DataBadge kind="missing" label="Not tracked" /> : freshnessBadge(c.freshness, c.isSimulated),
    },
    { key: 'obs', header: 'Observations', align: 'right', sortValue: (c) => c.observations, cell: (c) => c.observations.toLocaleString() },
    { key: 'range', header: 'Coverage', sortValue: (c) => c.firstDate, cell: (c) => c.firstDate ? <span className="text-xs">{date(c.firstDate)} – {date(c.lastDate)}</span> : '—' },
    { key: 'adj', header: 'Adj. close', cell: (c) => c.observations === 0 ? '—' : c.hasAdjustedClose ? <Badge variant="secondary">Yes</Badge> : <Badge variant="outline" className="text-warn" title="Returns exclude dividends">Raw only</Badge> },
    { key: 'px', header: 'Latest price', align: 'right', sortValue: (c) => c.latestPrice, cell: (c) => <span title={c.latestPriceAsOfUtc ? dateTime(c.latestPriceAsOfUtc) : undefined}>{c.currency ? money(c.latestPrice, c.currency) : num(c.latestPrice)}</span> },
    { key: 'ret', header: 'Retrieved', sortValue: (c) => c.lastRetrievedAtUtc, cell: (c) => <span className="text-xs text-muted-foreground">{relativeTime(c.lastRetrievedAtUtc)}</span> },
    {
      key: 'msg', header: 'Last sync',
      // Successful syncs show "OK"; routine notes (e.g. a history refresh after a dividend) stay available as a tooltip.
      cell: (c) => c.lastSyncSuccess === false
        ? <span className="text-xs text-loss" title={c.lastSyncMessage ?? ''}>Failed: {(c.lastSyncMessage ?? '').slice(0, 60)}</span>
        : <span className="text-xs text-muted-foreground" title={c.lastSyncMessage && c.lastSyncMessage !== 'Already up to date.' ? c.lastSyncMessage : undefined}>{c.lastSyncSuccess ? 'OK' : '—'}</span>,
    },
    {
      key: 'enabled', header: 'Enabled',
      cell: (c) => (
        <span onClick={(e) => e.stopPropagation()} onKeyDown={(e) => e.stopPropagation()}>
          <Switch checked={c.isEnabled} aria-label={`${c.isEnabled ? 'Disable' : 'Enable'} ${c.symbol}`}
            onCheckedChange={(on) => update.mutate({ symbol: c.symbol, isEnabled: on }, { onError: (e) => toast.error(errorMessage(e)) })} />
        </span>
      ),
    },
    {
      key: 'act', header: '',
      cell: (c) => downloading.has(c.symbol)
        ? <MatrixLoader className="size-5 text-[var(--brand)] dark:text-[var(--brand-accent)]" label={`Downloading ${c.symbol}`} />
        : <Button size="xs" variant="ghost" disabled={!!running} onClick={(e) => { e.stopPropagation(); runSync([c.symbol], true) }}>{c.observations === 0 ? 'Download' : 'Refresh'}</Button>,
    },
  ]

  function historyStart() {
    const start = new Date()
    start.setFullYear(start.getFullYear() - Number(years))
    return start.toISOString().slice(0, 10)
  }

  async function runSync(symbols?: string[], force = false, assetTypes?: ('Equity' | 'Etf' | 'Index')[]) {
    try {
      const job = await sync.mutateAsync({ symbols, assetTypes, startDate: historyStart(), forceFullRefresh: force })
      const what = describeSymbols(job.symbols, data?.coverage ?? [])
      toast.success('Download queued', { description: isAdministrator ? `${what} from ${job.source}` : what })
    } catch (e) {
      toast.error('Could not start download', { description: errorMessage(e) })
    }
  }

  /** The sync of the open page: its tracked instruments, or every exchange rate (only what is missing is downloaded). */
  async function syncSection() {
    if (current.path === 'currencies') {
      try {
        const r = await fxSync.mutateAsync({ startDate: historyStart() })
        if (r.started) toast.success('Exchange-rate download started', { description: `${r.currencies.length} currencies since ${date(r.from)}` })
        else toast.info('Exchange rates are complete', { description: `Every rate since ${date(r.from)} is already stored.` })
      } catch (e) {
        toast.error('Could not start the exchange-rate download', { description: errorMessage(e) })
      }
    } else {
      await runSync(undefined, false, current.path === 'indexes' ? ['Index'] : ['Equity', 'Etf'])
    }
  }
  const sectionBusy = current.path === 'currencies' ? !!data?.fxDownloadRunning || fxSync.isPending : !!running || sync.isPending

  if (isLoading) return <LoadingBlock height="h-96" />
  if (error || !data) return <ErrorState error={error} onRetry={() => refetch()} />

  return (
    <>
      <PageHeader
        title="Market data"
        description={<>{isAdministrator && <>Active source <strong>{data.activeProvider}</strong> · </>}Prices older than {data.stalePriceDays} business days are flagged stale.</>}
        actions={
          <>
            <Select value={years} onValueChange={(v) => setYears(String(v))} items={historyOptions}>
              <SelectTrigger className="w-32" aria-label="History length"><SelectValue /></SelectTrigger>
              <SelectContent>{historyOptions.map((o) => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
            </Select>
            {/* Each page syncs its own data: tracked stocks & ETFs, tracked indexes, or all exchange rates (administrators). */}
            {isAdministrator && <Button onClick={syncSection} disabled={sectionBusy}>Sync {current.syncLabel}</Button>}
            <Button variant="outline" onClick={() => setAddOpen(true)}>Add asset</Button>
          </>
        }
      />

      {data.activeProviderIsSimulated && (
        <div className="mb-4 flex items-center gap-2 rounded-lg border border-series-2/50 bg-series-2/10 px-3 py-2 text-sm">
          <DataBadge kind="simulated" /> Prices are synthetic, not real market data.{isAdministrator && ' Switch to a live provider in Administration › Settings.'}
        </div>
      )}

      {data.fxDownloadRunning && (
        <WorkingPanel className="mb-4" title="Downloading exchange rates…" detail="Missing daily reference rates, in two-year steps per currency; the list updates as they arrive." />
      )}

      {running && (
        <WorkingPanel className="mb-4"
          title={running.status === 'Queued' ? 'Download queued – waiting to start…' : 'Downloading price history…'}
          percent={running.status === 'Running' ? ((running.assetsCompleted + running.assetsFailed) / Math.max(1, running.assetsTotal)) * 100 : null}
          detail={[
            // Every type the job contains (the scheduled refresh takes all tracked stocks, ETFs and indexes), never just "stocks".
            `${(running.assetsCompleted + running.assetsFailed).toLocaleString()} of ${running.assetsTotal.toLocaleString()} tracked instruments (${describeSymbols(running.symbols, data.coverage)})`,
            isAdministrator ? `from ${running.source}` : null,
            running.logs.length ? `last: ${running.logs[running.logs.length - 1].symbol}` : null,
            running.assetsFailed ? `${running.assetsFailed} failed` : null,
            queued ? `${queued} more download${queued === 1 ? '' : 's'} waiting (downloads run one after another)` : null,
          ].filter(Boolean).join(' · ')} />
      )}

      <nav aria-label="Market data sections" className="nav-scroll -mx-1 mb-4 overflow-x-auto border-b">
        <ul className="flex min-w-max gap-0.5 px-1">
          {SECTIONS.map((x) => (
            <li key={x.path}>
              <Link to={x.path ? `/market-data/${x.path}` : '/market-data'} aria-current={x.path === current.path ? 'page' : undefined}
                className={cn(
                  'inline-flex border-b-2 px-3 py-2 text-sm whitespace-nowrap transition-colors focus-visible:rounded-t-md focus-visible:ring-2 focus-visible:ring-ring focus-visible:outline-none focus-visible:ring-inset',
                  x.path === current.path ? 'border-primary font-semibold text-foreground' : 'border-transparent text-muted-foreground hover:border-border hover:text-foreground',
                )}>
                {x.label}<span className="ml-1.5 text-xs text-muted-foreground tabular">{x.path === 'currencies' ? currencies.length.toLocaleString() : `${trackedTotal(x.path === 'indexes' ? indexes : stocks).toLocaleString()} tracked`}</span>
              </Link>
            </li>
          ))}
        </ul>
      </nav>

      {current.path === '' && (
        <SearchableTable key="stocks" label="stocks & ETFs" hint={current.hint} rows={stocks} columns={columns} rowKey={(c) => c.symbol} initialSort={{ key: 'symbol', dir: 'asc' }}
          matches={(c, q) => c.symbol.toLowerCase().includes(q) || (c.name ?? '').toLowerCase().includes(q) || c.currency.toLowerCase() === q}
          placeholder="Search symbol, name or currency" breakdown={catalogueLine(stocks)} />
      )}
      {current.path === 'indexes' && (
        <SearchableTable key="indexes" label="indexes" hint={current.hint} rows={indexes} columns={columns} rowKey={(c) => c.symbol} initialSort={{ key: 'symbol', dir: 'asc' }}
          matches={(c, q) => c.symbol.toLowerCase().includes(q) || (c.name ?? '').toLowerCase().includes(q) || c.currency.toLowerCase() === q}
          placeholder="Search symbol, name or currency" breakdown={catalogueLine(indexes)} />
      )}
      {current.path === 'currencies' && (
        <SearchableTable key="currencies" label="currencies" hint={current.hint} rows={currencies} rowKey={(c) => c.currency} initialSort={{ key: 'currency', dir: 'asc' }}
          matches={(c, q) => c.currency.toLowerCase().includes(q)} placeholder="Search currency code, e.g. JPY"
          empty="No exchange rates stored yet. They are downloaded when an analysis or a trade needs a conversion."
          columns={[
    { key: 'currency', header: 'Currency', sortValue: (c) => c.currency, cell: (c) => <span className="font-medium">{c.currency}</span> },
    { key: 'status', header: 'Status', sortValue: (c) => c.freshness, cell: (c) => freshnessBadge(c.freshness, c.isSimulated) },
    { key: 'rate', header: '1 EUR =', align: 'right', sortValue: (c) => c.latestUnitsPerEur, cell: (c) => <span title={`Rate of ${date(c.lastDate)}`}>{num(c.latestUnitsPerEur, 4)} {c.currency}</span> },
    { key: 'obs', header: 'Observations', align: 'right', sortValue: (c) => c.observations, cell: (c) => c.observations.toLocaleString() },
    { key: 'range', header: 'Coverage', sortValue: (c) => c.firstDate, cell: (c) => <span className="text-xs">{date(c.firstDate)} – {date(c.lastDate)}</span> },
    { key: 'ret', header: 'Retrieved', sortValue: (c) => c.lastRetrievedAtUtc, cell: (c) => <span className="text-xs text-muted-foreground">{relativeTime(c.lastRetrievedAtUtc)}</span> },
    ...(isAdministrator ? [{ key: 'src', header: 'Source', cell: (c: FxCoverage) => <span className="text-xs">{c.source}</span> }] : []),
  ]} />
      )}

      <div className={cn('mt-6 grid gap-4', isAdministrator && 'lg:grid-cols-2')}>
        {isAdministrator && <Card>
          <CardHeader><CardTitle className="text-sm">Providers</CardTitle><CardDescription className="text-xs">API keys are configured on the server only and never sent to the browser.</CardDescription></CardHeader>
          <CardContent className="space-y-3 text-sm">
            {data.providers.map((p) => (
              <div key={p.name} className="rounded-md border p-3">
                <div className="flex items-center justify-between gap-2">
                  <span className="font-medium">{p.displayName}</span>
                  <span className="flex gap-1">
                    {p.name === data.activeProvider && <Badge>Active</Badge>}
                    {p.isSimulated && <DataBadge kind="simulated" />}
                    <Badge variant={p.isConfigured ? 'secondary' : 'outline'}>{p.isConfigured ? 'Configured' : p.requiresApiKey ? 'API key missing' : 'Disabled'}</Badge>
                  </span>
                </div>
                <p className="mt-1 text-xs text-muted-foreground">{p.rateLimit}</p>
                <p className="text-xs text-muted-foreground">{p.terms} {p.providesAdjustedClose ? 'Provides adjusted closes.' : 'Raw closes only.'}</p>
              </div>
            ))}
          </CardContent>
        </Card>}
        <Card>
          <CardHeader><CardTitle className="text-sm">Recent download jobs</CardTitle></CardHeader>
          <CardContent className="space-y-2 text-sm">
            {data.recentJobs.length === 0 && <p className="text-muted-foreground">No downloads yet.</p>}
            {data.recentJobs.map((j) => (
              <details key={j.id} className="rounded-md border px-3 py-2">
                <summary className="flex cursor-pointer items-center justify-between gap-2">
                  <span>{isAdministrator && `${j.source} · `}{describeSymbols(j.symbols, data.coverage)}</span>
                  <span className="flex items-center gap-2 text-xs text-muted-foreground">{dateTime(j.requestedAtUtc)}<Badge variant={j.status === 'Failed' ? 'destructive' : j.status === 'Succeeded' ? 'secondary' : 'outline'}>{j.status}</Badge></span>
                </summary>
                <p className="mt-1 text-xs text-muted-foreground">{j.message}</p>
                <table className="mt-2 w-full text-xs tabular">
                  <thead className="text-muted-foreground"><tr><th className="text-left">Symbol</th><th className="text-right">Rcvd</th><th className="text-right">New</th><th className="text-right">Upd</th><th className="text-right">Rejected</th><th className="text-right">Dupl.</th></tr></thead>
                  <tbody>
                    {j.logs.map((l) => (
                      <tr key={l.symbol} className={l.success ? '' : 'text-loss'} title={l.message ?? ''}>
                        <td>{l.symbol}</td><td className="text-right">{l.rowsReceived}</td><td className="text-right">{l.rowsInserted}</td><td className="text-right">{l.rowsUpdated}</td><td className="text-right">{l.rowsRejected}</td><td className="text-right">{l.duplicatesSkipped}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </details>
            ))}
          </CardContent>
        </Card>
      </div>

      <AddAssetDialog open={addOpen} onOpenChange={setAddOpen} onAdded={(s) => runSync([s])} />
    </>
  )
}

/** The three pages of Market Data; each lists its instruments with the same search and paging. */
const SECTIONS = [
  { path: '', syncLabel: 'stocks & ETFs', label: 'Stocks & ETFs', hint: 'Every stock and index ETF of the catalogue – the same instruments as New portfolio. Not tracked: no prices downloaded yet; Download fetches them and keeps them fresh.' },
  { path: 'indexes', syncLabel: 'indexes', label: 'Indexes', hint: 'Stock indices used as benchmarks (e.g. ^GSPC, ^GDAXI). They are not investable; their prices are index points in the currency of their market.' },
  { path: 'currencies', syncLabel: 'currencies', label: 'Currencies', hint: 'Daily reference exchange rates, stored as units per 1 EUR – the pivot of every conversion: any other pair is calculated from two of these rates of the same day. Missing history is downloaded when an analysis needs it.' },
] as const

/**
 * The count line of an instrument list: per type, tracked and not tracked (src/lib/instrument-counts.ts), then the
 * catalogue total and how many have stored prices – never one unexplained total.
 */
function catalogueLine(rows: AssetCoverage[]) {
  const withPrices = rows.filter((c) => c.observations > 0).length
  return `${describeCatalogue(rows)} · ${rows.length.toLocaleString()} in the catalogue, ${withPrices.toLocaleString()} with prices`
}

/** Freshness of the latest stored day (same rule for prices and exchange rates). */
function freshnessBadge(freshness: DataFreshness, simulated: boolean) {
  return freshness === 'Missing' ? <DataBadge kind="missing" /> : simulated ? <DataBadge kind="simulated" /> : freshness === 'Stale' ? <DataBadge kind="stale" /> : <DataBadge kind="live" label="Fresh" />
}

const assetSchema = z.object({
  symbol: z.string().trim().regex(/^[A-Za-z0-9.\-^=]{1,15}$/, 'Letters, digits, ".", "-", "^" or "=" (max 15)'),
  name: z.string().max(200).optional(),
  sector: z.string().max(100).optional(),
  assetType: z.enum(['Equity', 'Etf']),
})
type AssetForm = z.infer<typeof assetSchema>

function AddAssetDialog({ open, onOpenChange, onAdded }: { open: boolean; onOpenChange: (o: boolean) => void; onAdded: (symbol: string) => void }) {
  const create = useCreateAsset()
  const { register, handleSubmit, reset, setValue, watch, formState: { errors } } = useForm<AssetForm>({ resolver: zodResolver(assetSchema), defaultValues: { assetType: 'Equity' } })
  const onSubmit = handleSubmit(async (v) => {
    try {
      const a = await create.mutateAsync({ symbol: v.symbol.toUpperCase(), name: v.name || undefined, sector: v.sector || undefined, assetType: v.assetType })
      toast.success(`${a.symbol} added`, { description: 'Downloading its price history…' })
      reset()
      onOpenChange(false)
      onAdded(a.symbol)
    } catch (e) {
      toast.error('Could not add asset', { description: errorMessage(e) })
    }
  })
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Add asset to the universe</DialogTitle>
          <DialogDescription>Listings in any supported currency; prices stay in that currency and are converted for analyses. Prices are downloaded right after adding.</DialogDescription>
        </DialogHeader>
        <form onSubmit={onSubmit} className="space-y-3" noValidate id="add-asset">
          <div className="space-y-1.5">
            <Label htmlFor="sym">Symbol</Label>
            <Input id="sym" placeholder="e.g. COST" autoFocus aria-invalid={!!errors.symbol} {...register('symbol')} />
            {errors.symbol && <p role="alert" className="text-xs text-destructive">{errors.symbol.message}</p>}
          </div>
          <div className="space-y-1.5"><Label htmlFor="nm">Name (optional – filled from provider)</Label><Input id="nm" {...register('name')} /></div>
          <div className="space-y-1.5"><Label htmlFor="sec">Sector (optional, used for sector limits)</Label><Input id="sec" {...register('sector')} /></div>
          <div className="space-y-1.5">
            <Label>Type</Label>
            <Select value={watch('assetType')} onValueChange={(v) => setValue('assetType', v as 'Equity' | 'Etf')} items={[{ value: 'Equity', label: 'Equity' }, { value: 'Etf', label: 'ETF' }]}>
              <SelectTrigger className="w-full" aria-label="Asset type"><SelectValue /></SelectTrigger>
              <SelectContent><SelectItem value="Equity">Equity</SelectItem><SelectItem value="Etf">ETF</SelectItem></SelectContent>
            </Select>
          </div>
        </form>
        <DialogFooter>
          <Button type="submit" form="add-asset" disabled={create.isPending}>Add asset</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
